using Shouldly;
using Teezy.Core.History;
using Xunit;

namespace Teezy.Core.Tests;

public class SpeedStatsTests
{
    private static readonly DateOnly Today = new(2026, 8, 24);
    private static readonly DateTimeOffset When = new(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);

    private static HistoryEntry Timed(double transcribe, double cleanup, double inject, double audio = 4) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        At = When,
        Text = "one two three four",
        AudioSeconds = audio,
        TranscribeMs = transcribe,
        CleanupMs = cleanup,
        InjectMs = inject,
    };

    private static HistoryEntry Untimed() => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        At = When,
        Text = "recorded before stages were split out",
        AudioSeconds = 4,
    };

    [Fact]
    public void NoTimedDictationsMeansNoSpeedFigures()
    {
        var stats = UsageStats.From([Untimed(), Untimed()], Today);

        // Zero, not a median of nothing — the card hides on this rather than printing 0 ms.
        stats.TimedDictations.ShouldBe(0);
        stats.MedianTranscribeMs.ShouldBe(0);
    }

    [Fact]
    public void OlderEntriesAreExcludedRatherThanCountedAsZero()
    {
        // The distinction that matters: an entry with no recorded timing is unknown, and
        // averaging it in as zero would report the machine as faster than it is.
        var stats = UsageStats.From([Timed(400, 0, 10), Untimed(), Untimed()], Today);

        stats.TimedDictations.ShouldBe(1);
        stats.MedianTranscribeMs.ShouldBe(400);
    }

    [Fact]
    public void TakesTheMiddleValueOfAnOddNumber() =>
        UsageStats.From([Timed(100, 0, 5), Timed(900, 0, 5), Timed(300, 0, 5)], Today)
            .MedianTranscribeMs.ShouldBe(300);

    [Fact]
    public void AveragesTheMiddleTwoOfAnEvenNumber() =>
        UsageStats.From([Timed(100, 0, 5), Timed(200, 0, 5), Timed(300, 0, 5), Timed(500, 0, 5)], Today)
            .MedianTranscribeMs.ShouldBe(250);

    [Fact]
    public void OneTimeoutDoesNotDragTheTypicalWaitWithIt()
    {
        // The whole reason for a median. A six-second timeout among four ordinary calls would
        // put the mean at 1.6 s — a number no individual dictation ever was.
        var stats = UsageStats.From(
            [Timed(300, 400, 5), Timed(300, 450, 5), Timed(300, 400, 5), Timed(300, 6000, 5)],
            Today);

        stats.MedianCleanupMs.ShouldBe(425);
        stats.SlowestCleanupMs.ShouldBe(6000);
    }

    [Fact]
    public void ReportsSpeedAsAMultipleOfRealtime() =>
        // 4 s of audio transcribed in 400 ms is 10x. The figure worth comparing between
        // machines, because it divides out how long you happened to talk for.
        UsageStats.From([Timed(400, 0, 5, audio: 4)], Today)
            .RealtimeFactor.ShouldBe(10, 0.01);

    [Fact]
    public void IgnoresUtterancesTooShortForTheRatioToMeanAnything()
    {
        var stats = UsageStats.From(
            [Timed(400, 0, 5, audio: 4), Timed(50, 0, 5, audio: 0.2)],
            Today);

        // Both are timed; only the long one feeds the realtime figure.
        stats.TimedDictations.ShouldBe(2);
        stats.RealtimeFactor.ShouldBe(10, 0.01);
    }
}
