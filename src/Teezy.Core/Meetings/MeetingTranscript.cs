using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Teezy.Core.Meetings;

/// <summary>Which recording a line came from.</summary>
/// <remarks>
/// Not a speaker's identity. The microphone is you and the speakers are everyone else, which
/// is a free and reliable split — but "everyone else" is one mixed stream, and naming the
/// people in it is information only Teams has.
/// </remarks>
public enum Side
{
    Me,
    Them,
}

/// <summary>One stretch of transcribed speech, placed in the meeting.</summary>
/// <param name="Speaker">
/// Who is talking, when the voices could be told apart: "Speaker 2", or whatever they have since
/// been renamed to. Null means nobody worked it out, and the line reads as "Them".
/// </param>
public sealed record MeetingLine(TimeSpan At, TimeSpan Duration, Side Side, string Text, string? Speaker = null)
{
    public TimeSpan End => At + Duration;

    /// <summary>What this line is labelled with in the transcript.</summary>
    public string Label => Side == Side.Me ? "Me" : Speaker ?? "Them";
}

/// <summary>How long a transcription took, which is the point of the first build.</summary>
/// <param name="Recorded">Length of the meeting.</param>
/// <param name="Speech">
/// How much audio was sent to the model, both sides added together — pauses are not sent. It
/// can exceed <paramref name="Recorded"/>: without a headset the microphone overhears the
/// speakers, so the same words are transcribed from both before the echo is dropped.
/// </param>
/// <param name="Took">Wall-clock time the transcription took.</param>
/// <param name="Pieces">How many pieces the meeting was cut into.</param>
/// <param name="EchoesDropped">Microphone lines dropped as the speakers being overheard.</param>
public sealed record TranscriptionStats(
    TimeSpan Recorded, TimeSpan Speech, TimeSpan Took, int Pieces, int EchoesDropped)
{
    /// <summary>
    /// How far behind the speakers their echo reached the microphone, or null when nothing of
    /// the sort was found — a headset, or a recording with nothing on one side.
    /// </summary>
    public TimeSpan? EchoDelay { get; init; }

    /// <summary>How much of the microphone was left out as the speakers, overheard.</summary>
    public TimeSpan EchoMuted { get; init; }

    /// <summary>How many voices were told apart at the far end; zero when nobody tried.</summary>
    public int Voices { get; init; }

    /// <summary>Meeting length over transcription time: 3 means an hour is ready in twenty minutes.</summary>
    [JsonIgnore]
    public double TimesRealtime => Took > TimeSpan.Zero ? Recorded / Took : 0;
}

/// <summary>Turns transcribed pieces from both recordings into one readable transcript.</summary>
public static partial class MeetingTranscript
{
    /// <summary>Pieces from the same side closer together than this read as one paragraph.</summary>
    private static readonly TimeSpan JoinGap = TimeSpan.FromSeconds(1.5);

    /// <summary>Drops microphone lines that are just the speakers, overheard.</summary>
    /// <remarks>
    /// <para>
    /// Without a headset the laptop's microphone hears the meeting coming out of its own
    /// speakers, so everything the other side says would appear twice — once as them, and once,
    /// slightly worse, as you. Teams cancels that echo for its own use; its cancelled audio is
    /// not something another app can have.
    /// </para>
    /// <para>
    /// The test is deliberately strict, because dropping something you actually said is worse
    /// than a duplicate: the line must overlap speech from the speakers for at least half its
    /// length, and most of its words must be words they said. Anything short is kept — "yes" is
    /// exactly the kind of thing you say over someone.
    /// </para>
    /// </remarks>
    /// <param name="acoustic">
    /// Whether the echo was also measured in the sound itself (see <see cref="EchoGate"/>). With
    /// that evidence the test here can be less strict, because it is no longer the only thing
    /// standing between a duplicate and the transcript.
    /// </param>
    public static IReadOnlyList<MeetingLine> DropEchoes(
        IReadOnlyList<MeetingLine> lines, out int dropped, bool acoustic = false)
    {
        var theirs = lines.Where(l => l.Side == Side.Them).ToList();
        var kept = new List<MeetingLine>(lines.Count);
        dropped = 0;

        foreach (var line in lines)
        {
            if (line.Side == Side.Me && IsEcho(line, theirs, acoustic))
            {
                dropped++;
                continue;
            }

            kept.Add(line);
        }

        return kept;
    }

    private static bool IsEcho(MeetingLine mine, IReadOnlyList<MeetingLine> theirs, bool acoustic)
    {
        var words = Words(mine.Text);
        if (words.Count < (acoustic ? 2 : 3)) return false;

        var heard = new HashSet<string>(StringComparer.Ordinal);
        var overlap = TimeSpan.Zero;

        foreach (var line in theirs)
        {
            var shared = (mine.End < line.End ? mine.End : line.End) - (mine.At > line.At ? mine.At : line.At);
            if (shared <= TimeSpan.Zero) continue;

            overlap += shared;
            heard.UnionWith(Words(line.Text));
        }

        if (overlap < mine.Duration / 2) return false;

        return words.Count(heard.Contains) >= words.Count * (acoustic ? 0.45 : 0.6);
    }

    /// <summary>Orders lines by time and joins a side's pieces that run on from each other.</summary>
    public static IReadOnlyList<MeetingLine> Merge(IEnumerable<MeetingLine> lines)
    {
        var merged = new List<MeetingLine>();

        foreach (var line in lines.OrderBy(l => l.At))
        {
            // Same voice, not merely the same recording: two people at the far end taking turns
            // quickly must not be run together into one paragraph with one of their names on it.
            if (merged.Count > 0 && merged[^1] is var last
                && last.Side == line.Side && last.Speaker == line.Speaker && line.At - last.End < JoinGap)
            {
                var end = line.End > last.End ? line.End : last.End;
                merged[^1] = last with { Duration = end - last.At, Text = $"{last.Text} {line.Text}" };
            }
            else
            {
                merged.Add(line);
            }
        }

        return merged;
    }

    /// <summary>The transcript as a plain text file, readable in Notepad.</summary>
    public static string Render(
        DateTimeOffset started,
        IReadOnlyList<MeetingLine> lines,
        TranscriptionStats stats,
        IEnumerable<string> notes)
    {
        var text = new StringBuilder();
        text.Append("# Meeting, ")
            .AppendLine(started.ToLocalTime().ToString("dddd d MMMM yyyy, h:mm tt", CultureInfo.CurrentCulture))
            .AppendLine();

        text.AppendLine(CultureInfo.InvariantCulture,
            $"Recorded {Clock(stats.Recorded)} · {Clock(stats.Speech)} of audio transcribed · took {Clock(stats.Took)} on this computer ({stats.TimesRealtime:0.0}x realtime)");

        // Said plainly, because a wrong measurement would otherwise be a mystery: a delay of
        // nothing on a laptop without a headset means the gate found no echo to remove.
        if (stats.EchoDelay is { } delay)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"The microphone was overhearing the speakers {delay.TotalMilliseconds:0} ms behind · {Clock(stats.EchoMuted)} of it left out");
        }

        foreach (var note in notes)
        {
            text.AppendLine().Append("Note: ").AppendLine(note);
        }

        text.AppendLine();

        if (lines.Count == 0)
        {
            text.AppendLine("Nothing was heard.");
        }

        foreach (var line in lines)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"[{Stamp(line.At)}] {line.Label}: {line.Text}")
                .AppendLine();
        }

        return text.ToString();
    }

    /// <summary>The spoken lines back out of a transcript file written by <see cref="Render"/>.</summary>
    /// <remarks>
    /// The file is the record of what was said, so the summary and the PDF read it rather than
    /// a second copy kept somewhere else. Lengths are not in the file and come back as zero.
    /// </remarks>
    public static IReadOnlyList<MeetingLine> ParseLines(string transcript) =>
    [
        .. LinePattern().Matches(transcript).Select(m => new MeetingLine(
            new TimeSpan(
                int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)),
            TimeSpan.Zero,
            m.Groups[4].Value == "Me" ? Side.Me : Side.Them,
            m.Groups[5].Value.Trim(),
            m.Groups[4].Value is "Me" or "Them" ? null : m.Groups[4].Value)),
    ];

    /// <summary>
    /// Everyone who is not you, as the transcript labels them. What the page offers to rename.
    /// </summary>
    public static IReadOnlyList<string> Speakers(string transcript) =>
    [
        .. ParseLines(transcript)
            .Where(l => l.Speaker is { Length: > 0 })
            .Select(l => l.Speaker!)
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>Renames one speaker everywhere in a transcript, leaving the words alone.</summary>
    public static string Rename(string transcript, string from, string to) =>
        RenamePattern(from).Replace(transcript, $"] {to.Trim()}: ");

    // A label is whatever sits between "] " and the first colon: "Me", "Them", "Speaker 2",
    // or a name someone has given it. Names with a colon in them are not names.
    [GeneratedRegex(@"^\[(\d+):(\d\d):(\d\d)\] ([^:\r\n]+): (.+?)\r?$", RegexOptions.Multiline)]
    private static partial Regex LinePattern();

    private static Regex RenamePattern(string label) =>
        new($@"\]\s{Regex.Escape(label.Trim())}:\s", RegexOptions.None, TimeSpan.FromSeconds(2));

    /// <summary>"4:05" under an hour, "1:02:09" over it.</summary>
    public static string Clock(TimeSpan t) => t.TotalHours >= 1
        ? string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}")
        : string.Create(CultureInfo.InvariantCulture, $"{t.Minutes}:{t.Seconds:00}");

    /// <summary>A fixed-width position in the meeting, "00:14:03".</summary>
    public static string Stamp(TimeSpan t) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}");

    private static HashSet<string> Words(string text) =>
        [.. WordPattern().Matches(text).Select(m => m.Value.Trim('\'').ToLowerInvariant()).Where(w => w.Length > 0)];

    [GeneratedRegex(@"[\p{L}\p{N}']+")]
    private static partial Regex WordPattern();
}
