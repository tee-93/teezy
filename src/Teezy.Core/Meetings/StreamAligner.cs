using Teezy.Core.Abstractions;

namespace Teezy.Core.Meetings;

/// <summary>Keeps a captured stream's sample position in step with the wall clock.</summary>
/// <remarks>
/// <para>
/// <b>Speaker loopback goes quiet by delivering nothing at all.</b> While nothing is playing,
/// WASAPI raises no data for a loopback capture — not zeros, no callbacks. Written straight to
/// disk, every pause in the meeting would vanish from the "them" file, and by the end of an
/// hour their words would be timestamped many minutes earlier than yours, next to the wrong
/// half of the conversation.
/// </para>
/// <para>
/// So when a chunk arrives after a gap, the silence it stands for is written first. Only after
/// a gap, deliberately: comparing every chunk against the clock would also react to the tiny
/// drift between a sound card's clock and the system's, and splice slivers of silence into the
/// middle of words. A microphone delivers continuously, so it is never touched.
/// </para>
/// </remarks>
public sealed class StreamAligner(Func<TimeSpan> elapsed)
{
    /// <summary>Gaps shorter than this are ordinary capture buffering, not silence.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMilliseconds(250);

    private TimeSpan? _lastArrival;

    /// <summary>Samples accounted for so far, silence included.</summary>
    public long Position { get; private set; }

    /// <summary>How much silence to write before a chunk of <paramref name="incoming"/> samples.</summary>
    public long SilenceBefore(int incoming)
    {
        var now = elapsed();
        var gap = _lastArrival is { } last ? now - last : now;
        _lastArrival = now;

        long silence = 0;
        if (gap > Tolerance)
        {
            // The chunk that ends a gap is its most recent audio, so it belongs at the end of
            // the time that passed, not the start.
            var expected = (long)(now.TotalSeconds * AudioChunk.SampleRate);
            silence = Math.Max(0, expected - (Position + incoming));
        }

        Position += silence + incoming;
        return silence;
    }
}
