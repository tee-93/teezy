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
public sealed record MeetingLine(TimeSpan At, TimeSpan Duration, Side Side, string Text)
{
    public TimeSpan End => At + Duration;
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
    public static IReadOnlyList<MeetingLine> DropEchoes(IReadOnlyList<MeetingLine> lines, out int dropped)
    {
        var theirs = lines.Where(l => l.Side == Side.Them).ToList();
        var kept = new List<MeetingLine>(lines.Count);
        dropped = 0;

        foreach (var line in lines)
        {
            if (line.Side == Side.Me && IsEcho(line, theirs))
            {
                dropped++;
                continue;
            }

            kept.Add(line);
        }

        return kept;
    }

    private static bool IsEcho(MeetingLine mine, IReadOnlyList<MeetingLine> theirs)
    {
        var words = Words(mine.Text);
        if (words.Count < 3) return false;

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

        return words.Count(heard.Contains) >= words.Count * 0.6;
    }

    /// <summary>Orders lines by time and joins a side's pieces that run on from each other.</summary>
    public static IReadOnlyList<MeetingLine> Merge(IEnumerable<MeetingLine> lines)
    {
        var merged = new List<MeetingLine>();

        foreach (var line in lines.OrderBy(l => l.At))
        {
            if (merged.Count > 0 && merged[^1] is var last
                && last.Side == line.Side && line.At - last.End < JoinGap)
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
            text.AppendLine(CultureInfo.InvariantCulture,
                    $"[{Stamp(line.At)}] {(line.Side == Side.Me ? "Me" : "Them")}: {line.Text}")
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
            m.Groups[5].Value.Trim())),
    ];

    [GeneratedRegex(@"^\[(\d+):(\d\d):(\d\d)\] (Me|Them): (.+?)\r?$", RegexOptions.Multiline)]
    private static partial Regex LinePattern();

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
