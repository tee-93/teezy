namespace Teezy.Core.History;

/// <summary>One app and how much dictation went into it.</summary>
public sealed record AppUsage(string App, int Count, double Fraction);

/// <summary>Everything the Insights page shows, computed from the history log.</summary>
/// <remarks>
/// Derived rather than accumulated. Keeping running totals in a settings file would be
/// cheaper, but they drift the moment an entry is deleted and cannot be recomputed after a
/// bug — recomputing from the log is always correct and, at a few thousand entries, free.
/// </remarks>
public sealed record UsageStats
{
    public int TotalWords { get; init; }
    public int TotalDictations { get; init; }
    public int TotalCorrections { get; init; }

    /// <summary>Average speaking rate, weighted by time rather than by utterance.</summary>
    /// <remarks>
    /// Weighted deliberately: a plain mean of per-utterance rates lets a two-word "yes" count
    /// as much as a two-minute paragraph, which flatters short bursts and misreports the
    /// number badly.
    /// </remarks>
    public int WordsPerMinute { get; init; }

    public double TotalSpokenSeconds { get; init; }

    /// <summary>Consecutive days ending today or yesterday.</summary>
    public int CurrentStreak { get; init; }
    public int LongestStreak { get; init; }

    /// <summary>Words per local day, for the heatmap.</summary>
    public IReadOnlyDictionary<DateOnly, int> WordsByDay { get; init; } =
        new Dictionary<DateOnly, int>();

    public IReadOnlyList<AppUsage> Apps { get; init; } = [];

    /// <summary>
    /// Time saved against typing, in minutes.
    /// </summary>
    /// <remarks>
    /// Against <b>40 wpm</b>, an average sustained typing rate for prose. Stated rather than
    /// hidden because the number is meaningless without it — a fast touch typist should read
    /// it as an upper bound.
    /// </remarks>
    public const int AssumedTypingWpm = 40;

    public double MinutesSavedVsTyping =>
        TotalWords / (double)AssumedTypingWpm - TotalSpokenSeconds / 60.0;

    public static UsageStats Empty { get; } = new();

    /// <param name="today">Injected so streaks are testable without touching the clock.</param>
    public static UsageStats From(IEnumerable<HistoryEntry> entries, DateOnly today)
    {
        var list = entries as IReadOnlyList<HistoryEntry> ?? entries.ToList();
        if (list.Count == 0) return Empty;

        var totalWords = 0;
        var totalCorrections = 0;
        var spokenSeconds = 0.0;
        var wordsInTimedUtterances = 0;
        var timedSeconds = 0.0;
        var byDay = new Dictionary<DateOnly, int>();
        var byApp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in list)
        {
            var words = e.WordCount;
            totalWords += words;
            totalCorrections += e.Corrections;
            spokenSeconds += e.AudioSeconds;

            // Only utterances long enough to have a meaningful rate feed the average.
            if (e.AudioSeconds >= 0.5)
            {
                wordsInTimedUtterances += words;
                timedSeconds += e.AudioSeconds;
            }

            var day = DateOnly.FromDateTime(e.At.LocalDateTime);
            byDay[day] = byDay.GetValueOrDefault(day) + words;

            var app = string.IsNullOrWhiteSpace(e.App) ? "Other" : e.App;
            byApp[app] = byApp.GetValueOrDefault(app) + 1;
        }

        var (current, longest) = Streaks([.. byDay.Keys], today);

        return new UsageStats
        {
            TotalWords = totalWords,
            TotalDictations = list.Count,
            TotalCorrections = totalCorrections,
            TotalSpokenSeconds = spokenSeconds,
            WordsPerMinute = timedSeconds > 0
                ? (int)Math.Round(wordsInTimedUtterances / timedSeconds * 60.0)
                : 0,
            CurrentStreak = current,
            LongestStreak = longest,
            WordsByDay = byDay,
            Apps = [.. byApp
                .OrderByDescending(kv => kv.Value)
                .Select(kv => new AppUsage(kv.Key, kv.Value, kv.Value / (double)list.Count))],
        };
    }

    /// <summary>Current and longest runs of consecutive active days.</summary>
    /// <remarks>
    /// The current streak counts back from <b>yesterday</b> as well as today. Requiring an
    /// entry today would reset the streak to zero every midnight and show 0 to someone who
    /// dictated all of the previous day but has not started yet this morning.
    /// </remarks>
    private static (int Current, int Longest) Streaks(List<DateOnly> days, DateOnly today)
    {
        if (days.Count == 0) return (0, 0);

        days.Sort();

        var longest = 1;
        var run = 1;
        for (var i = 1; i < days.Count; i++)
        {
            run = days[i] == days[i - 1].AddDays(1) ? run + 1 : 1;
            longest = Math.Max(longest, run);
        }

        var active = new HashSet<DateOnly>(days);
        var anchor = active.Contains(today) ? today
            : active.Contains(today.AddDays(-1)) ? today.AddDays(-1)
            : (DateOnly?)null;

        var current = 0;
        for (var d = anchor; d is { } day && active.Contains(day); d = day.AddDays(-1))
        {
            current++;
        }

        return (current, longest);
    }
}
