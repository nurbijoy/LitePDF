using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

namespace LitePdf.App.Infrastructure;

public sealed partial class SingleInstance : IDisposable
{
    private const uint ASFW_ANY = unchecked((uint)-1);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint dwProcessId);

    private static readonly string MutexName = $"Local\\LitePDF-SingleInstance-{Environment.UserName}";
    private static readonly string PipeName = $"LitePDF-IPC-{Environment.UserName}";

    private readonly Mutex _mutex;
    private readonly bool _isFirstInstance;
    private CancellationTokenSource? _serverCts;

    private SingleInstance(Mutex mutex, bool isFirstInstance)
    {
        _mutex = mutex;
        _isFirstInstance = isFirstInstance;
    }

    public bool IsFirstInstance => _isFirstInstance;

    public static SingleInstance TryCreate()
    {
        bool isFirstInstance = false;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(true, MutexName, out bool createdNew);
            isFirstInstance = createdNew;
            if (!createdNew)
            {
                try
                {
                    isFirstInstance = mutex.WaitOne(0, false);
                }
                catch (AbandonedMutexException)
                {
                    isFirstInstance = true;
                }
            }
        }
        catch (AbandonedMutexException)
        {
            isFirstInstance = true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create or acquire single-instance mutex");
        }

        mutex ??= new Mutex(false);
        return new SingleInstance(mutex, isFirstInstance);
    }

    /// <summary>
    /// Sends arguments to the running primary instance. Returns true if delivered successfully.
    /// </summary>
    public static bool SendToExistingInstance(string? filePath, int timeoutMs = 800)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: false);
            writer.WriteLine(filePath ?? "");
            writer.Flush();
            AllowSetForegroundWindow(ASFW_ANY);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to send file to existing instance via pipe");
            return false;
        }
    }

    /// <summary>
    /// Starts the asynchronous IPC server loop on the primary instance.
    /// </summary>
    public void StartServer(Action<string> onFileReceived)
    {
        var cts = _serverCts = new CancellationTokenSource();
        _ = Task.Run(() => ServerLoopAsync(cts.Token, onFileReceived));
    }

    private static async Task ServerLoopAsync(CancellationToken ct, Action<string> onFileReceived)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is not null)
                {
                    onFileReceived(line);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Single instance IPC server error");
                try
                {
                    await Task.Delay(300, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _serverCts?.Cancel();
            _serverCts?.Dispose();
        }
        catch
        {
        }

        try
        {
            if (_isFirstInstance)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
        }

        _mutex.Dispose();
    }
}
