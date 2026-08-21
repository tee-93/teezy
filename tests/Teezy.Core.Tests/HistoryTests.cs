using Shouldly;
using Teezy.Core.History;
using Xunit;

namespace Teezy.Core.Tests;

public class HistoryTests
{
    private static HistoryEntry Entry(
        string text = "hello world", double seconds = 2, DateTimeOffset? at = null,
        string? app = null, int corrections = 0) =>
        new()
        {
            Id = Guid.NewGuid().ToString("N"),
            At = at ?? DateTimeOffset.Now,
            Text = text,
            AudioSeconds = seconds,
            App = app,
            Corrections = corrections,
        };

    // ---- store ----

    [Fact]
    public void RoundTripsNewestFirst()
    {
        var path = Path.Combine(Path.GetTempPath(), $"teezy-{Guid.NewGuid():N}.jsonl");
        var store = new HistoryStore(path);

        store.Add(Entry("first", at: DateTimeOffset.Now.AddMinutes(-5)));
        store.Add(Entry("second", at: DateTimeOffset.Now));

        var loaded = store.Load();
        loaded.Count.ShouldBe(2);
        loaded[0].Text.ShouldBe("second");   // newest first

        File.Delete(path);
    }

    [Fact]
    public void SurvivesATornFinalLine()
    {
        // An interrupted append leaves a half-written line. Losing one utterance is fine;
        // refusing to show any history is not.
        var path = Path.Combine(Path.GetTempPath(), $"teezy-{Guid.NewGuid():N}.jsonl");
        var store = new HistoryStore(path);
        store.Add(Entry("intact"));
        File.AppendAllText(path, "{\"Id\":\"broken\",\"Te");

        var loaded = store.Load();
        loaded.Count.ShouldBe(1);
        loaded[0].Text.ShouldBe("intact");

        File.Delete(path);
    }

    [Fact]
    public void TextWithNewlinesAndQuotesSurvives()
    {
        // JSON Lines breaks if a record's own newlines reach the file unescaped.
        var path = Path.Combine(Path.GetTempPath(), $"teezy-{Guid.NewGuid():N}.jsonl");
        var store = new HistoryStore(path);
        const string awkward = "Line one.\nLine \"two\" — café.";
        store.Add(Entry(awkward));

        store.Load().ShouldHaveSingleItem().Text.ShouldBe(awkward);
        File.Delete(path);
    }

    [Fact]
    public void MissingFileIsEmptyNotAnError() =>
        new HistoryStore(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.jsonl"))
            .Load().ShouldBeEmpty();

    // ---- word counting ----

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("one", 1)]
    [InlineData("one two three", 3)]
    [InlineData("line one\nline two", 4)]
    public void CountsWords(string text, int expected) =>
        HistoryEntry.CountWords(text).ShouldBe(expected);

    [Fact]
    public void ImplausiblyShortHoldsReportNoRate() =>
        // Three words in 0.2 s is 900 wpm; letting that into an average wrecks it.
        Entry("a b c", seconds: 0.2).WordsPerMinute.ShouldBe(0);

    // ---- stats ----

    [Fact]
    public void WordsPerMinuteIsWeightedByTimeNotByUtterance()
    {
        // 2 words in 60 s, then 200 words in 60 s. Weighted = 101 wpm.
        // A plain mean of the two rates would say 101 too, so make them differ:
        // 2 words in 6 s (20 wpm) and 300 words in 60 s (300 wpm).
        // Weighted: 302 words / 66 s = 274.5 wpm. Unweighted mean: 160 wpm.
        var stats = UsageStats.From(
        [
            Entry(string.Join(' ', Enumerable.Repeat("x", 2)), seconds: 6),
            Entry(string.Join(' ', Enumerable.Repeat("x", 300)), seconds: 60),
        ], DateOnly.FromDateTime(DateTime.Today));

        stats.WordsPerMinute.ShouldBe(275);
    }

    [Fact]
    public void CountsWordsAndCorrections()
    {
        var stats = UsageStats.From(
        [
            Entry("one two three", corrections: 2),
            Entry("four five", corrections: 1),
        ], DateOnly.FromDateTime(DateTime.Today));

        stats.TotalWords.ShouldBe(5);
        stats.TotalDictations.ShouldBe(2);
        stats.TotalCorrections.ShouldBe(3);
    }

    [Fact]
    public void StreakCountsConsecutiveDaysEndingToday()
    {
        var today = new DateOnly(2026, 8, 21);
        var stats = UsageStats.From(
        [
            Entry(at: On(today)), Entry(at: On(today.AddDays(-1))), Entry(at: On(today.AddDays(-2))),
            // gap at -3
            Entry(at: On(today.AddDays(-4))),
        ], today);

        stats.CurrentStreak.ShouldBe(3);
    }

    [Fact]
    public void StreakSurvivesNotHavingDictatedYetToday()
    {
        // Requiring an entry today would reset the streak to zero every midnight.
        var today = new DateOnly(2026, 8, 21);
        var stats = UsageStats.From(
        [
            Entry(at: On(today.AddDays(-1))), Entry(at: On(today.AddDays(-2))),
        ], today);

        stats.CurrentStreak.ShouldBe(2);
    }

    [Fact]
    public void StreakIsBrokenByTwoIdleDays()
    {
        var today = new DateOnly(2026, 8, 21);
        var stats = UsageStats.From([Entry(at: On(today.AddDays(-2)))], today);
        stats.CurrentStreak.ShouldBe(0);
    }

    [Fact]
    public void LongestStreakIsFoundAnywhereInHistory()
    {
        var today = new DateOnly(2026, 8, 21);
        var days = new[] { -20, -19, -18, -17, -10, -1, 0 };
        var stats = UsageStats.From(
            [.. days.Select(d => Entry(at: On(today.AddDays(d))))], today);

        stats.LongestStreak.ShouldBe(4);
        stats.CurrentStreak.ShouldBe(2);
    }

    [Fact]
    public void MultipleEntriesInOneDayAreOneStreakDay()
    {
        var today = new DateOnly(2026, 8, 21);
        var stats = UsageStats.From(
            [Entry(at: On(today)), Entry(at: On(today)), Entry(at: On(today))], today);

        stats.CurrentStreak.ShouldBe(1);
        stats.LongestStreak.ShouldBe(1);
    }

    [Fact]
    public void GroupsByAppAndFallsBackToOther()
    {
        var stats = UsageStats.From(
        [
            Entry(app: "chrome"), Entry(app: "chrome"), Entry(app: "Code"), Entry(app: null),
        ], DateOnly.FromDateTime(DateTime.Today));

        stats.Apps[0].App.ShouldBe("chrome");
        stats.Apps[0].Count.ShouldBe(2);
        stats.Apps[0].Fraction.ShouldBe(0.5);
        stats.Apps.ShouldContain(a => a.App == "Other");
    }

    [Fact]
    public void EmptyHistoryIsAllZeroes()
    {
        var stats = UsageStats.From([], DateOnly.FromDateTime(DateTime.Today));
        stats.TotalWords.ShouldBe(0);
        stats.CurrentStreak.ShouldBe(0);
        stats.WordsPerMinute.ShouldBe(0);
    }

    private static DateTimeOffset On(DateOnly day) =>
        new(day.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);
}
