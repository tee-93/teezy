using Shouldly;
using Teezy.Core.Cost;
using Teezy.Core.History;
using Xunit;

namespace Teezy.Core.Tests;

public class CostTests
{
    private static readonly TokenUsage Call = new(1000, 500);

    // ---- rates ----

    [Fact]
    public void PricesSonnetAtTheIntroductoryRateWhileItApplies() =>
        // $2/$10 per Mtok: 1000 in + 500 out = 0.2c + 0.5c.
        ModelRates.Cost("claude-sonnet-5", Call, new DateOnly(2026, 8, 23))
            .ShouldBe(0.007m);

    [Fact]
    public void PricesSonnetAtTheStandardRateAfterTheIntroductoryPeriod() =>
        // The same call costs half as much again from September. A single hardcoded rate
        // would report one of these two numbers for every dictation ever recorded.
        ModelRates.Cost("claude-sonnet-5", Call, new DateOnly(2026, 9, 1))
            .ShouldBe(0.0105m);

    [Fact]
    public void PricesTheLastDayOfTheIntroductoryPeriodAsIntroductory() =>
        ModelRates.Cost("claude-sonnet-5", Call, new DateOnly(2026, 8, 31))
            .ShouldBe(0.007m);

    [Fact]
    public void ReturnsNullForAModelItDoesNotKnow() =>
        // Null, not zero. A model this build has never heard of costs an unknown amount, and
        // folding it in as free would under-report the total without saying so.
        ModelRates.Cost("some-model-shipped-next-year", Call, new DateOnly(2026, 8, 23))
            .ShouldBeNull();

    [Fact]
    public void ReturnsNullWhenNothingWasSpent() =>
        ModelRates.Cost("claude-sonnet-5", TokenUsage.None, new DateOnly(2026, 8, 23))
            .ShouldBeNull();

    // ---- aggregation ----

    private static HistoryEntry Entry(DateTimeOffset at, TokenUsage? tokens, string? model) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        At = at,
        Text = "one two three",
        AudioSeconds = 2,
        Tokens = tokens,
        Model = model,
    };

    [Fact]
    public void CountsOnlyTheDictationsThatReachedTheApi()
    {
        var today = new DateOnly(2026, 8, 23);
        var when = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

        var stats = UsageStats.From(
            [
                Entry(when, Call, "claude-sonnet-5"),
                Entry(when, null, null),           // cleanup was off
                Entry(when, TokenUsage.None, "claude-sonnet-5"),  // no call was made
            ],
            today);

        stats.TotalDictations.ShouldBe(3);
        stats.ClaudeCalls.ShouldBe(1);
        stats.TotalTokens.Total.ShouldBe(1500);
    }

    [Fact]
    public void SeparatesThisMonthFromAllTime()
    {
        var today = new DateOnly(2026, 8, 23);

        var stats = UsageStats.From(
            [
                Entry(new DateTimeOffset(2026, 8, 2, 9, 0, 0, TimeSpan.Zero), Call, "claude-sonnet-5"),
                Entry(new DateTimeOffset(2026, 7, 2, 9, 0, 0, TimeSpan.Zero), Call, "claude-sonnet-5"),
            ],
            today);

        stats.CostUsd.ShouldBe(0.014m);
        stats.CostThisMonthUsd.ShouldBe(0.007m);
    }

    [Fact]
    public void DeclaresCallsItCouldNotPriceInsteadOfDroppingThem()
    {
        var when = new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

        var stats = UsageStats.From(
            [
                Entry(when, Call, "claude-sonnet-5"),
                Entry(when, Call, "a-model-from-the-future"),
            ],
            new DateOnly(2026, 8, 23));

        stats.ClaudeCalls.ShouldBe(2);
        stats.UnpricedCalls.ShouldBe(1);
        stats.CostUsd.ShouldBe(0.007m);

        // The unpriceable model reports no cost at all rather than a partial one.
        stats.Models.Single(m => m.Model == "a-model-from-the-future").CostUsd.ShouldBeNull();
    }

    [Fact]
    public void PricesEachEntryAtTheRateThatAppliedOnItsOwnDay()
    {
        // One before the introductory rate lapsed and one after. Re-pricing history at
        // today's rate would quietly rewrite what was actually billed.
        var stats = UsageStats.From(
            [
                Entry(new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero), Call, "claude-sonnet-5"),
                Entry(new DateTimeOffset(2026, 9, 20, 9, 0, 0, TimeSpan.Zero), Call, "claude-sonnet-5"),
            ],
            new DateOnly(2026, 9, 21));

        stats.CostUsd.ShouldBe(0.0175m);
    }

    [Fact]
    public void TokenUsageAddsUp() =>
        (new TokenUsage(10, 20, 30, 40) + new TokenUsage(1, 2, 3, 4))
            .ShouldBe(new TokenUsage(11, 22, 33, 44));
}
