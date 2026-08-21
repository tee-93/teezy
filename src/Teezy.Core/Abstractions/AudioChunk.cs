namespace Teezy.Core.Abstractions;

/// <summary>A block of captured audio, always 16 kHz mono float32 in [-1, 1].</summary>
/// <remarks>
/// <para>
/// The format is fixed rather than negotiated because Parakeet accepts exactly one, and
/// WASAPI shared mode will resample to it on request. Converting in one place — at the
/// device — beats threading a format through every layer.
/// </para>
/// <para>
/// <b>The samples array must be freshly allocated by the producer.</b> Capture APIs hand
/// back a buffer they recycle the instant the callback returns, so a chunk that borrows
/// that memory is a race that shows up as garbled transcripts under load, not as a crash.
/// </para>
/// </remarks>
public sealed record AudioChunk(float[] Samples)
{
    public const int SampleRate = 16_000;

    /// <summary>Wall-clock duration this chunk represents.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}
