namespace Teezy.Core.Meetings;

/// <summary>One stretch of a recording in which one person is talking.</summary>
/// <param name="Speaker">
/// Which voice, numbered from zero in the order they are found. Not a name: telling two voices
/// apart is all this is — who they belong to is something only the people in the call know.
/// </param>
public readonly record struct SpeakerSpan(TimeSpan Start, TimeSpan End, int Speaker)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Tidying and reading of what a <see cref="IDiariser"/> found.</summary>
public static class SpeakerSpans
{
    /// <summary>A stretch shorter than this is not a turn; it is the model changing its mind.</summary>
    private static readonly TimeSpan TooShort = TimeSpan.FromSeconds(0.7);

    /// <summary>
    /// Drops flickers and joins what is left, so two people talking read as turns rather than
    /// as a stutter. A half-second of "the other one" inside a sentence is nearly always the
    /// model wavering on a word, and a transcript that changes speaker mid-sentence is worse
    /// than one that misses a brief interjection.
    /// </summary>
    public static IReadOnlyList<SpeakerSpan> Smooth(IEnumerable<SpeakerSpan> spans)
    {
        var kept = spans.Where(s => s.Duration >= TooShort).OrderBy(s => s.Start).ToList();
        var joined = new List<SpeakerSpan>();

        foreach (var span in kept)
        {
            if (joined.Count > 0 && joined[^1] is var last
                && last.Speaker == span.Speaker && span.Start - last.End < TooShort)
            {
                joined[^1] = last with { End = span.End > last.End ? span.End : last.End };
            }
            else
            {
                joined.Add(span);
            }
        }

        return joined;
    }

    /// <summary>
    /// Which voice was talking for most of a stretch of transcript, or null when none of them
    /// were — the words then stay as they were, simply "Them".
    /// </summary>
    public static int? At(IReadOnlyList<SpeakerSpan> spans, TimeSpan from, TimeSpan to)
    {
        int? best = null;
        var longest = TimeSpan.Zero;

        foreach (var span in spans)
        {
            var shared = (to < span.End ? to : span.End) - (from > span.Start ? from : span.Start);
            if (shared <= longest) continue;

            longest = shared;
            best = span.Speaker;
        }

        return longest > TimeSpan.Zero ? best : null;
    }

    /// <summary>"Speaker 1", "Speaker 2" — what a voice is called before anyone names it.</summary>
    public static string Name(int speaker) => $"Speaker {speaker + 1}";
}

/// <summary>Tells apart the voices in one recording.</summary>
/// <remarks>
/// <para>
/// The microphone recording is always you, so this is only ever asked about the speaker
/// recording: it is the far end that arrives as one mixed stream of however many people are on
/// the call.
/// </para>
/// <para>
/// Kept out of <c>Teezy.Core</c>'s way like <see cref="Abstractions.ITranscriber"/>: the models
/// and the library that runs them live in <c>Teezy.Speech</c>, so the meeting logic can be
/// tested without either.
/// </para>
/// </remarks>
public interface IDiariser
{
    /// <summary>Whether the models this needs have been downloaded.</summary>
    bool IsAvailable { get; }

    /// <summary>The stretches of one voice each, in order. Empty when it cannot say.</summary>
    Task<IReadOnlyList<SpeakerSpan>> SplitAsync(string wavPath, CancellationToken ct = default);
}
