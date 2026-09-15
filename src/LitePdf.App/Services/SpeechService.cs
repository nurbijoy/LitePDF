using System.Windows;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace LitePdf.App.Services;

/// <summary>Reads text aloud with Windows' on-device voices (T-67).</summary>
public sealed class SpeechService : IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly MediaPlayer _player = new();

    /// <param name="host">Unused; kept so callers don't change. WinRT MediaPlayer needs no visual host.</param>
    public async Task SpeakAsync(string text, FrameworkElement host)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        Stop();
        var stream = await _synth.SynthesizeTextToStreamAsync(text);
        _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
        _player.Play();
    }

    public void Stop()
    {
        _player.Pause();
        _player.Source = null;
    }

    public void Dispose()
    {
        _player.Dispose();
        _synth.Dispose();
    }
}
