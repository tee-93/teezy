using Shouldly;
using Teezy.Core.History;
using Teezy.Core.Home;
using Teezy.Core.Quotes;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>What the pipeline looks like from Home and from the morning briefing.</summary>
public class QuoteHomeTests
{
    // Tuesday 22 September 2026, 9:00 am, Sydney time.
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.FromHours(10));
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static Quote Quote(string customer, long cents, int sentDaysAgo, int chased = 0, DateOnly? lastChased = null) =>
        Teezy.Core.Quotes.Quote.New(customer, "door hardware", cents, Today.AddDays(-sentDaysAgo), Now)
            with { Chased = chased, LastChased = lastChased };

    private static HomeSnapshot Snapshot(params Quote[] quotes) =>
        new(Now, [], UsageStats.Empty, [], Quotes: quotes);

    [Fact]
    public void TheTileShowsWhatIsOutAndWhatWantsChasing()
    {
        var tile = HomeTiles.Compute("quotes_open", Snapshot(
            Quote("Hunter Builders", 420_000, sentDaysAgo: 10),
            Quote("Orikan", 900_000, sentDaysAgo: 1)));

        tile.Value.ShouldBe(QuotePlan.Money(13200));
        tile.Caption.ShouldBe("1 to chase today");
        tile.Tone.ShouldBe(TileTone.Warning);
        tile.Target.ShouldBe(TileTarget.Quotes);
    }

    [Fact]
    public void WithNoQuotesTheTileSaysSoRatherThanZeroDollars()
    {
        var tile = HomeTiles.Compute("quotes_open", Snapshot());

        tile.Value.ShouldBe("—");
        tile.Caption.ShouldBe("No quotes out");
    }

    [Fact]
    public void TheMonthTileCountsWinsAndTheRate()
    {
        var won = Quote("Orikan", 1_000_000, sentDaysAgo: 30) with
        {
            Status = QuoteStatus.Won, Decided = new DateOnly(2026, 9, 12),
        };
        var lost = Quote("Cessnock", 200_000, sentDaysAgo: 40) with
        {
            Status = QuoteStatus.Lost, Decided = new DateOnly(2026, 9, 3),
        };

        var tile = HomeTiles.Compute("won_month", Snapshot(won, lost));

        tile.Value.ShouldBe(QuotePlan.Money(10000));
        tile.Caption.ShouldBe("1 of 2 · 50%");
        tile.Tone.ShouldBe(TileTone.Good);
    }

    [Fact]
    public void TheBriefingListsWhatToChaseAndWhatHasGoneQuiet()
    {
        var chase = Quote("Hunter Builders", 420_000, sentDaysAgo: 10);
        var quiet = Quote("Orikan", 900_000, sentDaysAgo: 60, chased: 3, lastChased: Today.AddDays(-25));

        var briefing = MorningBriefing.For(Snapshot(chase, quiet), "Zack");

        var section = briefing.Sections.ShouldHaveSingleItem();
        section.Title.ShouldBe("Quotes");
        section.Items.Count.ShouldBe(2);
        section.Items[0].Text.ShouldBe("Hunter Builders — door hardware");
        section.Items[0].Detail.ShouldContain("chase");
        section.Items[0].Late.ShouldBeTrue();
        section.Items[1].Detail.ShouldContain("quiet 25 days");
    }

    [Fact]
    public void AQuoteThatNeedsNothingTodayIsNotInTheBriefing()
    {
        MorningBriefing.For(Snapshot(Quote("Hunter Builders", 420_000, sentDaysAgo: 1)), "Zack")
            .Sections.ShouldBeEmpty();
    }

    [Fact]
    public void TheAiMaterialCarriesQuotesButNeverTheirEmails()
    {
        var quote = Quote("Hunter Builders", 420_000, sentDaysAgo: 10) with
        {
            Emails = [new Teezy.Core.Tasks.TaskEmail(Now, "Quote 1042", "priya@example.com", Now, "Our price is …")],
        };

        var material = MorningBriefing.Material(Snapshot(quote));

        material.ShouldContain("Hunter Builders");
        material.ShouldContain("door hardware");
        material.ShouldNotContain("Our price is");
        material.ShouldNotContain("priya@example.com");
    }
}
