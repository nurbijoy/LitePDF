using System.Collections.Concurrent;

namespace LitePdf.Pdfium;

/// <summary>
/// Single-threaded worker that serializes all PDFium calls.
/// Implements priority queue (lower value = higher priority).
/// See BLUEPRINT §4.
/// </summary>
public sealed class PdfiumWorker : IDisposable
{
    private sealed class WorkItem
    {
        public Func<CancellationToken, object?> Func = null!;
        public TaskCompletionSource<object?> Tcs = null!;
        public CancellationToken Ct;
        public int Priority;
        public long Seq;
    }

    private readonly Thread _thread;
    private readonly PriorityQueue<WorkItem, (int priority, long seq)> _queue = new();
    private readonly object _lock = new();
    private long _seq;
    private bool _disposed;
    private readonly ManualResetEventSlim _signal = new(false);
    private readonly TaskCompletionSource _initTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static PdfiumWorker Instance { get; } = new PdfiumWorker();

    private PdfiumWorker()
    {
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "PDFium"
        };
        _thread.Start();
        // Wait for init to complete synchronously? We do async init via task.
        // Ensure FPDF_InitLibrary runs on worker thread before any other work.
        _initTcs.Task.Wait(5000);
    }

    private void WorkerLoop()
    {
        try
        {
            NativeMethods.FPDF_InitLibrary();
            _initTcs.TrySetResult();
        }
        catch (Exception ex)
        {
            _initTcs.TrySetException(ex);
            return;
        }

        while (true)
        {
            WorkItem? item = null;
            lock (_lock)
            {
                if (_queue.Count > 0)
                {
                    item = _queue.Dequeue();
                }
                else
                {
                    if (_disposed) break;
                }
            }

            if (item == null)
            {
                _signal.Wait(100);
                _signal.Reset();
                lock (_lock)
                {
                    if (_disposed && _queue.Count == 0) break;
                }
                continue;
            }

            if (item.Ct.IsCancellationRequested)
            {
                item.Tcs.TrySetCanceled(item.Ct);
                continue;
            }

            try
            {
                var result = item.Func(item.Ct);
                item.Tcs.TrySetResult(result);
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == item.Ct || item.Ct.IsCancellationRequested)
            {
                item.Tcs.TrySetCanceled(item.Ct);
            }
            catch (Exception ex)
            {
                item.Tcs.TrySetException(ex);
            }
        }

        try { NativeMethods.FPDF_DestroyLibrary(); } catch { }
    }

    public Task<T> RunAsync<T>(Func<CancellationToken, T> func, int priority, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PdfiumWorker));

        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem
        {
            Func = c => func(c),
            Tcs = tcs,
            Ct = ct,
            Priority = priority,
            Seq = Interlocked.Increment(ref _seq)
        };

        lock (_lock)
        {
            _queue.Enqueue(item, (priority, item.Seq));
        }
        _signal.Set();

        // Convert to Task<T>
        return tcs.Task.ContinueWith(t =>
        {
            if (t.IsCanceled) throw new OperationCanceledException(ct);
            if (t.IsFaulted) throw t.Exception!.InnerException!;
            return (T)t.Result!;
        }, ct, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task<T> RunAsync<T>(Func<T> func, int priority, CancellationToken ct = default)
        => RunAsync(_ => func(), priority, ct);

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }
        _signal.Set();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(2000);
        }
        _signal.Dispose();
    }
}
