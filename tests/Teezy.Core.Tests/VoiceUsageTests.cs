using Shouldly;
using Teezy.Core.Voice;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Counting characters spoken, so a tier can be chosen on evidence.</summary>
public class VoiceUsageTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"teezy-voice-{Guid.NewGuid():N}.json");

    [Fact]
    public void CountsWhatWasSpoken()
    {
        var path = TempPath();
        try
        {
            var usage = new VoiceUsage(path);
            usage.Add(120);
            usage.Add(80);

            usage.ThisMonth.ShouldBe(200);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SurvivesARestart()
    {
        // The whole point is a figure gathered over weeks, so it has to outlive the process.
        var path = TempPath();
        try
        {
            new VoiceUsage(path).Add(500);

            new VoiceUsage(path).ThisMonth.ShouldBe(500);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NothingSpokenCountsNothing()
    {
        var path = TempPath();
        try
        {
            var usage = new VoiceUsage(path);
            usage.Add(0);
            usage.Add(-5);

            usage.ThisMonth.ShouldBe(0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AMissingFileIsNotAnError() =>
        // A counter is a convenience. Losing it means a worse tier recommendation, not a
        // broken app.
        new VoiceUsage(TempPath()).ThisMonth.ShouldBe(0);

    [Fact]
    public void ACorruptFileIsNotAnError()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{ this is not json");

            new VoiceUsage(path).ThisMonth.ShouldBe(0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MonthsAreKeptApart()
    {
        var path = TempPath();
        try
        {
            var now = DateOnly.FromDateTime(DateTime.Now);
            File.WriteAllText(path, $$"""{"{{now.AddMonths(-1):yyyy-MM}}": 4321}""");

            var usage = new VoiceUsage(path);
            usage.Add(100);

            usage.ThisMonth.ShouldBe(100);
            usage.LastMonth.ShouldBe(4321);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OnlyAYearIsKept()
    {
        // Older than that answers no question anyone is asking, and an unbounded file would
        // grow forever for no reason.
        var path = TempPath();
        try
        {
            var months = Enumerable.Range(1, 20)
                .Select(i => $"\"{DateTime.Now.AddMonths(-i):yyyy-MM}\": {i}");

            File.WriteAllText(path, "{" + string.Join(",", months) + "}");

            var usage = new VoiceUsage(path);
            usage.Add(1);

            var kept = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, int>>(File.ReadAllText(path));

            kept!.Count.ShouldBeLessThanOrEqualTo(12);

            // The most recent months are the ones that survive.
            kept.ShouldContainKey($"{DateTime.Now:yyyy-MM}");
        }
        finally { File.Delete(path); }
    }
}
