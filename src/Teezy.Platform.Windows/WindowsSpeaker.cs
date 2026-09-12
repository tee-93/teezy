using System.Globalization;
using System.Speech.Synthesis;
using Teezy.Core.Abstractions;

namespace Teezy.Platform.Windows;

/// <summary>Reads answers with the voice already on the machine.</summary>
/// <remarks>
/// <para>
/// SAPI, through <c>System.Speech</c>: free, offline, instant, and installed everywhere. It
/// will not be mistaken for a person, and that trade is deliberate — this tier exists so the
/// feature works with no key, no network and no cost. A voice worth listening to is what a paid
/// tier would be for.
/// </para>
/// <para>
/// It is better than it first appears, though, because the system default is usually the worst
/// voice installed. See <see cref="BestVoice"/>.
/// </para>
/// <para>
/// <b>Speaking is asynchronous and interruptible.</b> <see cref="SpeakAsync"/> queues and
/// returns; the session must not be held open for the length of a sentence, and the next key
/// press has to be able to cut it off.
/// </para>
/// </remarks>
public sealed class WindowsSpeaker : ISpeaker
{
    private readonly Lazy<SpeechSynthesizer?> _synth = new(Create);

    private static SpeechSynthesizer? Create()
    {
        try
        {
            var synth = new SpeechSynthesizer();
            synth.SetOutputToDefaultAudioDevice();

            // A little above default. Speech synthesis at its natural rate reads as ponderous
            // next to a pill that appeared instantly.
            synth.Rate = 1;

            if (BestVoice(synth) is { } voice) synth.SelectVoice(voice);

            return synth;
        }
        catch (Exception e) when (e is PlatformNotSupportedException or InvalidOperationException)
        {
            // A machine with no voices installed is rare but real, and it must not take the
            // assistant down with it — the answer is still on screen.
            return null;
        }
    }

    /// <summary>
    /// The least robotic voice available, in the user's own accent where there is one.
    /// </summary>
    /// <remarks>
    /// <b>Not the system default, which is usually the worst one installed.</b> Measured here:
    /// the default was "Microsoft Hazel Desktop" while George, James, Catherine and Susan sat
    /// unused beside it. The "Desktop" suffix marks the old SAPI5 voices; the ones without it
    /// are the newer, markedly better-sounding set, and preferring them costs nothing.
    /// <para>
    /// Accent is matched where possible for the same reason a British user would rather not be
    /// read to in American — it is the difference between a voice and a robot.
    /// </para>
    /// </remarks>
    private static string? BestVoice(SpeechSynthesizer synth)
    {
        var installed = synth.GetInstalledVoices()
            .Where(v => v.Enabled)
            .Select(v => v.VoiceInfo)
            .ToList();

        if (installed.Count == 0) return null;

        var modern = installed
            .Where(v => !v.Name.EndsWith(" Desktop", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var candidates = modern.Count > 0 ? modern : installed;
        var culture = CultureInfo.CurrentUICulture.Name;

        return (candidates.FirstOrDefault(v => v.Culture.Name == culture)
                ?? candidates.FirstOrDefault(v =>
                    v.Culture.TwoLetterISOLanguageName == CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
                ?? candidates[0]).Name;
    }

    public bool IsAvailable => _synth.Value is not null;

    public Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (_synth.Value is not { } synth || string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;

        try
        {
            // Cancel what is in flight before queueing, or answers stack up and are read one
            // after another long after anyone wanted them.
            synth.SpeakAsyncCancelAll();
            synth.SpeakAsync(text);
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // Losing the voice is not worth losing the answer over; it is already on screen.
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (_synth.Value is not { } synth) return;

        try { synth.SpeakAsyncCancelAll(); }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (!_synth.IsValueCreated || _synth.Value is not { } synth) return;

        try { synth.SpeakAsyncCancelAll(); }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException) { }

        synth.Dispose();
    }
}
