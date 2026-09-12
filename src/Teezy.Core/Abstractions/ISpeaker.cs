namespace Teezy.Core.Abstractions;

/// <summary>A voice that could read your answers.</summary>
/// <param name="Name">The identifier, and what is persisted in settings.</param>
/// <param name="Culture">Display name of its language and accent, e.g. "English (Australia)".</param>
/// <param name="Gender">As the voice describes itself. Shown, not interpreted.</param>
/// <param name="IsModern">
/// False for the older SAPI5 set, which Windows marks with a "Desktop" suffix and which sound
/// markedly worse. Surfaced rather than hidden so a choice can be made knowingly.
/// </param>
public sealed record SpeechVoice(string Name, string Culture, string Gender, bool IsModern);

/// <summary>Reads an answer out loud.</summary>
/// <remarks>
/// <para>
/// For <i>answers</i>, not confirmations. "Volume set to forty percent" takes two seconds to
/// say for something the pill shows instantly and silently, and you would hear it twenty times
/// a day; an actual answer to a question is genuinely better heard than read.
/// </para>
/// <para>
/// <see cref="SpeakAsync"/> returns as soon as the words are queued, not when they finish.
/// The pill has already moved on by then, and a caller made to await the end of a sentence
/// would hold the voice session open for the length of it.
/// </para>
/// </remarks>
public interface ISpeaker : IDisposable
{
    /// <summary>Whether it is switched on and able to speak.</summary>
    bool IsAvailable { get; }

    /// <summary>Voices installed on this machine, for the settings picker.</summary>
    IReadOnlyList<SpeechVoice> Voices();

    /// <summary>
    /// The voice to use, by <see cref="SpeechVoice.Name"/>. Null picks the best available.
    /// </summary>
    /// <remarks>
    /// Automatic is the default and is worth keeping: the <i>system</i> default is usually the
    /// oldest voice installed, so choosing for the user beats inheriting that. This exists for
    /// when they disagree, which is a matter of taste and not something to be argued with.
    /// </remarks>
    string? PreferredVoice { get; set; }

    /// <summary>The voice actually in use, for Settings to show. Null if none could be opened.</summary>
    string? VoiceName { get; }

    /// <summary>Begins reading, replacing anything already being read.</summary>
    Task SpeakAsync(string text, CancellationToken ct = default);

    /// <summary>
    /// Stops mid-sentence.
    /// </summary>
    /// <remarks>
    /// Called when a new utterance starts. Someone who presses the key again has stopped
    /// listening to the last answer, and talking over them is the rudest thing this could do.
    /// </remarks>
    void Stop();
}
