using LitePdf.Pdfium.Interop;

namespace LitePdf.Pdfium;

/// <summary>
/// PDFium is not thread-safe: every native call runs on this one dedicated thread, in priority order
/// (lower value first, FIFO within a priority). Cancelled items complete immediately and are skipped.
/// </summary>
public sealed class PdfiumWorker
{
    private static readonly Lazy<PdfiumWorker> LazyInstance = new(() => new PdfiumWorker());

    private readonly PriorityQueue<WorkItem, (int Priority, long Sequence)> _queue = new();
    private readonly object _gate = new();
    private long _sequence;
    private Exception? _initError;

    private PdfiumWorker()
    {
        var thread = new Thread(Run) { IsBackground = true, Name = "PDFium worker" };
        thread.Start();
    }

    public static PdfiumWorker Instance => LazyInstance.Value;

    public int ManagedThreadId { get; private set; }

    public Task<T> RunAsync<T>(Func<T> work, int priority, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<T>(ct);
        var item = new WorkItem<T>(work, ct);
        lock (_gate)
        {
            _queue.Enqueue(item, (priority, _sequence++));
            Monitor.Pulse(_gate);
        }
        return item.Task;
    }

    public Task RunAsync(Action work, int priority, CancellationToken ct = default) =>
        RunAsync(() => { work(); return true; }, priority, ct);

    private void Run()
    {
        ManagedThreadId = Environment.CurrentManagedThreadId;
        try
        {
            NativeMethods.FPDF_InitLibrary();
        }
        catch (Exception ex)
        {
            _initError = new InvalidOperationException("The PDF engine (pdfium.dll) could not be loaded.", ex);
        }

        while (true)
        {
            WorkItem item;
            lock (_gate)
            {
                while (_queue.Count == 0) Monitor.Wait(_gate);
                item = _queue.Dequeue();
            }
            item.Execute(_initError);
        }
    }

    private abstract class WorkItem
    {
        public abstract void Execute(Exception? initError);
    }

    private sealed class WorkItem<T> : WorkItem
    {
        private readonly Func<T> _work;
        private readonly CancellationToken _ct;
        private readonly TaskCompletionSource<T> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _registration;

        public WorkItem(Func<T> work, CancellationToken ct)
        {
            _work = work;
            _ct = ct;
            if (ct.CanBeCanceled)
                _registration = ct.Register(static s => ((WorkItem<T>)s!)._tcs.TrySetCanceled(((WorkItem<T>)s!)._ct), this);
        }

        public Task<T> Task => _tcs.Task;

        public override void Execute(Exception? initError)
        {
            try
            {
                if (_tcs.Task.IsCompleted) return;
                if (initError is not null)
                {
                    _tcs.TrySetException(initError);
                    return;
                }
                _tcs.TrySetResult(_work());
            }
            catch (OperationCanceledException) when (_ct.IsCancellationRequested)
            {
                _tcs.TrySetCanceled(_ct);
            }
            catch (Exception ex)
            {
                _tcs.TrySetException(ex);
            }
            finally
            {
                _registration.Dispose();
            }
        }
    }
}
