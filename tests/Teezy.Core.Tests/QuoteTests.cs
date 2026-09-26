using Shouldly;
using Teezy.Core.Quotes;
using Teezy.Core.Tasks;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>The arithmetic of a pipeline: when to chase, what has gone quiet, how the month went.</summary>
public class QuotePlanTests
{
    // Tuesday 22 September 2026.
    private static readonly DateOnly Today = new(2026, 9, 22);
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.FromHours(10));

    private static Quote Sent(int daysAgo, int chased = 0, long cents = 420_000) =>
        Quote.New("Hunter Builders", "door hardware", cents, Today.AddDays(-daysAgo), Now)
            with { Chased = chased };

    [Fact]
    public void TheFirstChaseIsThreeWorkingDaysAfterItWentOut()
    {
        // Sent Friday 18th: three days is Monday 21st.
        QuotePlan.NextChase(Sent(daysAgo: 4)).ShouldBe(new DateOnly(2026, 9, 21));
    }

    [Fact]
    public void AChaseThatWouldFallOnAWeekendWaitsForTheMonday()
    {
        // Sent Wednesday 16th: seven days is Wednesday 23rd; three days is Saturday 19th → Monday.
        QuotePlan.NextChase(Sent(daysAgo: 6)).ShouldBe(new DateOnly(2026, 9, 21));
    }

    [Fact]
    public void EachChaseBooksTheNextOneFurtherOut()
    {
        var quote = Sent(daysAgo: 20);

        QuotePlan.NextChase(quote with { Chased = 0 }).ShouldBe(TaskPlan.Workday(quote.Sent.AddDays(3)));
        QuotePlan.NextChase(quote with { Chased = 1 }).ShouldBe(TaskPlan.Workday(quote.Sent.AddDays(7)));
        QuotePlan.NextChase(quote with { Chased = 2 }).ShouldBe(TaskPlan.Workday(quote.Sent.AddDays(14)));
    }

    [Fact]
    public void OnceTheCadenceIsUsedUpItIsChasedMonthlyFromTheLastOne()
    {
        var quote = Sent(daysAgo: 40, chased: 3) with { LastChased = Today.AddDays(-10) };

        QuotePlan.NextChase(quote).ShouldBe(TaskPlan.Workday(Today.AddDays(20)));
    }

    [Fact]
    public void ADecidedQuoteIsNeverChasedAgain()
    {
        var won = Sent(daysAgo: 5) with { Status = QuoteStatus.Won, Decided = Today };

        QuotePlan.NextChase(won).ShouldBeNull();
        QuotePlan.BucketOf(won, Today).ShouldBe(QuoteBucket.Decided);
    }

    [Fact]
    public void ThreeWeeksWithNothingHappeningIsQuiet()
    {
        QuotePlan.IsQuiet(Sent(daysAgo: 20), Today).ShouldBeFalse();
        QuotePlan.IsQuiet(Sent(daysAgo: 21), Today).ShouldBeTrue();

        // Chased last week, so it is not quiet however old the quote is.
        QuotePlan.IsQuiet(Sent(daysAgo: 60, chased: 3) with { LastChased = Today.AddDays(-5) }, Today)
            .ShouldBeFalse();
    }

    [Fact]
    public void QuotesAreArrangedByWhatNeedsDoing()
    {
        var due = Sent(daysAgo: 10);                                   // chase overdue
        // Chased 25 days ago: nothing has happened since, but the monthly chase is not due yet.
        var quiet = Sent(daysAgo: 60, chased: 3) with { LastChased = Today.AddDays(-25) };
        var fresh = Sent(daysAgo: 1);                                  // nothing due yet
        var won = Sent(daysAgo: 30) with { Status = QuoteStatus.Won, Decided = Today };

        var arranged = QuotePlan.Arrange([fresh, won, quiet, due], Today);

        arranged.Select(g => g.Bucket)
            .ShouldBe([QuoteBucket.ToChase, QuoteBucket.Quiet, QuoteBucket.Open, QuoteBucket.Decided]);
        arranged[0].Quotes.ShouldHaveSingleItem().Id.ShouldBe(due.Id);
        arranged[1].Quotes.ShouldHaveSingleItem().Id.ShouldBe(quiet.Id);
    }

    [Fact]
    public void TheMonthAddsUpToWhatIsOutAndWhatWasDecided()
    {
        var open = Sent(daysAgo: 4, cents: 420_000);
        var wonThis = Sent(daysAgo: 30, cents: 1_000_000) with { Status = QuoteStatus.Won, Decided = new DateOnly(2026, 9, 10) };
        var lostThis = Sent(daysAgo: 40, cents: 250_000) with { Status = QuoteStatus.Lost, Decided = new DateOnly(2026, 9, 2) };
        var wonLast = Sent(daysAgo: 60, cents: 900_000) with { Status = QuoteStatus.Won, Decided = new DateOnly(2026, 8, 20) };

        var totals = QuotePlan.Totals([open, wonThis, lostThis, wonLast], Today);

        totals.Open.ShouldBe((1, 4200m));
        totals.Won.ShouldBe((1, 10000m));
        totals.Lost.ShouldBe((1, 2500m));
        totals.WinRate.ShouldBe(0.5);
    }

    [Fact]
    public void AMonthWithNothingDecidedHasNoWinRateRatherThanZero()
    {
        QuotePlan.Totals([Sent(daysAgo: 3)], Today).WinRate.ShouldBeNull();
    }

    [Fact]
    public void MoneyIsHeldInCentsAndNeverInBinaryFractions()
    {
        var quote = Quote.New("Orikan", "readers", 420_735, Today, Now);

        quote.Amount.ShouldBe(4207.35m);
        (quote.Amount * 3).ShouldBe(12622.05m);
    }
}
