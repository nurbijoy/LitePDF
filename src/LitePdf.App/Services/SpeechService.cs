#if WINDOWS
using Windows.Media.SpeechSynthesis;
using System.Windows.Controls;
using System.Windows;
using LitePdf.Core.Text;

namespace LitePdf.App.Services;

public sealed class SpeechService : IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private MediaElement? _media;

    public async Task SpeakAsync(string text, FrameworkElement host)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var stream = await _synth.SynthesizeTextToStreamAsync(text);
            if (_media == null)
            {
                _media = new MediaElement { Volume = 1.0 };
                // Add to visual tree hidden
                if (host is Panel panel)
                    panel.Children.Add(_media);
            }
            _media.SetSource(stream, stream.ContentType);
            _media.Play();
        }
        catch { }
    }

    public void Stop()
    {
        try { _media?.Stop(); } catch { }
    }

    public void Dispose()
    {
        _synth.Dispose();
    }
}
#else
namespace LitePdf.App.Services;
public sealed class SpeechService : IDisposable
{
    public System.Threading.Tasks.Task SpeakAsync(string text, System.Windows.FrameworkElement host) => System.Threading.Tasks.Task.CompletedTask;
    public void Stop() { }
    public void Dispose() { }
}
#endif
