using Teezy.Core.Cost;

namespace Teezy.Core.History;

/// <summary>One app and how much dictation went into it.</summary>
public sealed record AppUsage(string App, int Count, double Fraction);

/// <summary>What one model was asked to do, and what it cost.</summary>
/// <param name="CostUsd">Null when the rate table does not cover the model.</param>
public sealed record ModelSpend(string Model, int Calls, TokenUsage Tokens, decimal? CostUsd);

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

    // ---- What the Claude tier consumed ----

    /// <summary>Dictations that actually went to the API. Not every entry does — cleanup can
    /// be off, the tier can be off, and a failed call still produced text locally.</summary>
    public int ClaudeCalls { get; init; }

    public TokenUsage TotalTokens { get; init; } = TokenUsage.None;

    /// <summary>Everything that could be priced, in US dollars.</summary>
    /// <remarks>
    /// An estimate of what <b>Teezy</b> spent, not a copy of the bill: it cannot see anything
    /// else on the account, and it prices from a table that will eventually go stale.
    /// </remarks>
    public decimal CostUsd { get; init; }

    /// <summary>The same figure for the current calendar month, which is the one people
    /// actually want — a lifetime total answers a question nobody asked.</summary>
    public decimal CostThisMonthUsd { get; init; }

    /// <summary>Calls the rate table could not price, so the totals above can say so rather
    /// than quietly under-reporting.</summary>
    public int UnpricedCalls { get; init; }

    public IReadOnlyList<ModelSpend> Models { get; init; } = [];

    // ---- Where the wait goes ----

    /// <summary>Dictations that recorded a stage breakdown. Older ones did not.</summary>
    public int TimedDictations { get; init; }

    /// <summary>
    /// Typical milliseconds per stage — medians, not means.
    /// </summary>
    /// <remarks>
    /// A mean is the wrong summary here. One dictation that hit the cleanup timeout adds six
    /// seconds and drags the average somewhere no individual dictation ever was, which is
    /// precisely the number someone would then try to explain. The median says what a normal
    /// utterance costs; <see cref="SlowestTranscribeMs"/> covers the tail.
    /// </remarks>
    public double MedianTranscribeMs { get; init; }
    public double MedianCleanupMs { get; init; }
    public double MedianInjectMs { get; init; }

    public double SlowestTranscribeMs { get; init; }
    public double SlowestCleanupMs { get; init; }

    /// <summary>
    /// Median transcription speed as a multiple of realtime, or 0 when unknown.
    /// </summary>
    /// <remarks>
    /// The number worth comparing between machines, because it divides out how long you
    /// happened to talk for. Roughly 19x on the Snapdragon this was tuned on.
    /// </remarks>
    public double RealtimeFactor { get; init; }

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

        var transcribeMs = new List<double>();
        var cleanupMs = new List<double>();
        var injectMs = new List<double>();
        var realtimeFactors = new List<double>();

        var claudeCalls = 0;
        var totalTokens = TokenUsage.None;
        var costUsd = 0m;
        var costThisMonth = 0m;
        var unpriced = 0;
        var byModel = new Dictionary<string, (int Calls, TokenUsage Tokens, decimal Cost, bool AllPriced)>(
            StringComparer.OrdinalIgnoreCase);

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

            if (e.TranscribeMs is { } t)
            {
                transcribeMs.Add(t);

                // Only utterances long enough for the ratio to mean anything, matching the
                // rule the words-per-minute average already uses.
                if (e.AudioSeconds >= 0.5 && t > 0) realtimeFactors.Add(e.AudioSeconds * 1000.0 / t);
            }

            if (e.CleanupMs is { } c) cleanupMs.Add(c);
            if (e.InjectMs is { } i) injectMs.Add(i);

            if (e.Tokens is not { IsEmpty: false } tokens) continue;

            claudeCalls++;
            totalTokens += tokens;

            var cost = e.CostUsd;
            if (cost is { } spent)
            {
                costUsd += spent;
                if (day.Year == today.Year && day.Month == today.Month) costThisMonth += spent;
            }
            else
            {
                unpriced++;
            }

            var model = string.IsNullOrWhiteSpace(e.Model) ? "unknown" : e.Model;
            var prior = byModel.GetValueOrDefault(
                model, (Calls: 0, Tokens: TokenUsage.None, Cost: 0m, AllPriced: true));
            byModel[model] = (
                prior.Calls + 1,
                prior.Tokens + tokens,
                prior.Cost + (cost ?? 0m),
                prior.AllPriced && cost is not null);
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
            ClaudeCalls = claudeCalls,
            TotalTokens = totalTokens,
            CostUsd = costUsd,
            CostThisMonthUsd = costThisMonth,
            UnpricedCalls = unpriced,

            // A model with any unpriced call reports null rather than a partial total, which
            // would look like a complete one.
            Models = [.. byModel
                .OrderByDescending(kv => kv.Value.Calls)
                .Select(kv => new ModelSpend(
                    kv.Key, kv.Value.Calls, kv.Value.Tokens,
                    kv.Value.AllPriced ? kv.Value.Cost : null))],
            TimedDictations = transcribeMs.Count,
            MedianTranscribeMs = Median(transcribeMs),
            MedianCleanupMs = Median(cleanupMs),
            MedianInjectMs = Median(injectMs),
            SlowestTranscribeMs = transcribeMs.Count > 0 ? transcribeMs.Max() : 0,
            SlowestCleanupMs = cleanupMs.Count > 0 ? cleanupMs.Max() : 0,
            RealtimeFactor = Median(realtimeFactors),
        };
    }

    /// <summary>Middle value, or the mean of the middle two. Zero for an empty set.</summary>
    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;

        values.Sort();
        var mid = values.Count / 2;

        return values.Count % 2 == 1
            ? values[mid]
            : (values[mid - 1] + values[mid]) / 2.0;
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
