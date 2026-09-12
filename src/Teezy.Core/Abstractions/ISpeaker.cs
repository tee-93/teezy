namespace Teezy.Core.Abstractions;

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
