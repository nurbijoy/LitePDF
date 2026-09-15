using LitePdf.Core;
using LitePdf.Core.Text;
using LitePdf.Ocr;
using LitePdf.Pdfium;

namespace LitePdf.App.Services;

public sealed class OcrService
{
    private readonly WindowsOcrEngine _engine = new();
    private readonly Queue<int> _queue = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    public event Action<int, OcrPageResult>? PageOcred;
    public event Action<int>? Progress;
    public event Action? Completed;

    public bool IsAvailable => _engine.IsAvailable;
    public IReadOnlyList<string> Languages => _engine.AvailableLanguages;

    public void QueuePage(int pageIndex)
    {
        lock (_lock)
        {
            if (!_queue.Contains(pageIndex))
                _queue.Enqueue(pageIndex);
        }
        EnsureRunning();
    }

    public void QueueAll(int pageCount)
    {
        lock (_lock)
        {
            _queue.Clear();
            for (int i = 0; i < pageCount; i++)
                _queue.Enqueue(i);
        }
        EnsureRunning();
    }

    public void Cancel()
    {
        _cts?.Cancel();
    }

    private void EnsureRunning()
    {
        lock (_lock)
        {
            if (_running) return;
            _running = true;
            _cts = new CancellationTokenSource();
            Task.Run(() => RunAsync(_cts.Token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                int page;
                lock (_lock)
                {
                    if (_queue.Count == 0) break;
                    page = _queue.Dequeue();
                }
                if (ct.IsCancellationRequested) break;
                Progress?.Invoke(page);
                // The actual OCR work is done by caller via event? For now we just signal.
                await Task.Delay(10, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            lock (_lock) _running = false;
            Completed?.Invoke();
        }
    }
}
