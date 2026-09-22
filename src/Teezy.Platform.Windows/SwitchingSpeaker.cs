using Teezy.Core;
using Teezy.Core.Abstractions;

namespace Teezy.Platform.Windows;

/// <summary>Speaks with whichever voice tier is selected, and can fall back.</summary>
/// <remarks>
/// <para>
/// One <see cref="ISpeaker"/> for everything else to hold, so neither the app nor the settings
/// page has to know there are two tiers. Which one answers is a setting, read per utterance,
/// so switching applies immediately.
/// </para>
/// <para>
/// <b>The paid tier falls back to the local one rather than to silence.</b> A missing key, an
/// unset voice, an expired subscription or no network all end with something being said —
/// worse, but said. Silence would leave the user unsure whether the assistant heard them, which
/// is the one thing the voice exists to remove.
/// </para>
/// </remarks>
public sealed class SwitchingSpeaker(
    Func<TeezySettings> settings,
    WindowsSpeaker local,
    ElevenLabsSpeaker cloud,
    KokoroSpeaker natural) : ISpeaker
{
    /// <summary>The tier that would answer right now.</summary>
    public ISpeaker Active => settings().VoiceProvider switch
    {
        VoiceProvider.ElevenLabs when cloud.IsAvailable => cloud,
        VoiceProvider.Kokoro when natural.IsAvailable => natural,
        _ => local,
    };

    public bool IsAvailable => Active.IsAvailable;

    public IReadOnlyList<SpeechVoice> Voices() => Chosen.Voices();

    /// <summary>
    /// The tier the user has <i>asked</i> for, which is not always the one that can answer.
    /// </summary>
    /// <remarks>
    /// Settings must list and set the voices of the tier being configured, even while it is
    /// unusable — otherwise choosing a voice for a key you have just pasted is impossible,
    /// because until a voice is chosen the tier is not available, and until it is available
    /// its voices are not listed.
    /// </remarks>
    private ISpeaker Chosen =>
        settings().VoiceProvider switch
        {
            VoiceProvider.ElevenLabs => cloud,
            VoiceProvider.Kokoro => natural,
            _ => local,
        };

    public string? PreferredVoice
    {
        get => Chosen.PreferredVoice;
        set => Chosen.PreferredVoice = value;
    }

    public string? VoiceName => Chosen.VoiceName;

    public string? VoiceListError => Chosen.VoiceListError;

    public Task SpeakAsync(string text, CancellationToken ct = default) => Active.SpeakAsync(text, ct);

    public void Stop()
    {
        // All three, always. The tier may have changed since the last utterance began, and a
        // sentence left playing from the other one is exactly the rudeness Stop exists for.
        local.Stop();
        cloud.Stop();
        natural.Stop();
    }

    public void Dispose()
    {
        local.Dispose();
        cloud.Dispose();
        natural.Dispose();
    }
}
