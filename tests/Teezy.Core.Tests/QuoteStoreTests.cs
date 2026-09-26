using Shouldly;
using Teezy.Core.Quotes;
using Teezy.Core.Tasks;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>The quote file, and the chase tasks that keep step with it.</summary>
public sealed class QuoteStoreTests : IDisposable
{
    // Tuesday 22 September 2026.
    private static readonly DateOnly Today = new(2026, 9, 22);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "teezy-quotes-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 9, 22, 9, 0, 0, TimeSpan.FromHours(10));

    private QuoteStore Quotes(string file = "quotes.json") => new(Path.Combine(_folder, file), () => _now);

    private TaskStore Tasks(string file = "tasks.json") => new(Path.Combine(_folder, file), () => _now);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void AQuoteSurvivesARestartWithItsMoneyIntact()
    {
        var store = Quotes();
        var added = store.Add("Hunter Builders", "door hardware", 420_735, Today, reference: "Q-1042");

        var again = Quotes().Find(added.Id)!;

        again.Customer.ShouldBe("Hunter Builders");
        again.What.ShouldBe("door hardware");
        again.AmountCents.ShouldBe(420_735);
        again.Amount.ShouldBe(4207.35m);
        again.Reference.ShouldBe("Q-1042");
        again.Status.ShouldBe(QuoteStatus.Open);
    }

    [Fact]
    public void WinningAQuoteRecordsTheDayAndStopsItBeingOpen()
    {
        var store = Quotes();
        var quote = store.Add("Orikan", "readers", 1_000_000, Today.AddDays(-10));

        _now = _now.AddMinutes(1);
        var won = store.Decide(quote.Id, QuoteStatus.Won, Today)!;

        won.Status.ShouldBe(QuoteStatus.Won);
        won.Decided.ShouldBe(Today);
        won.IsOpen.ShouldBeFalse();
        store.Visible.ShouldHaveSingleItem();
    }

    [Fact]
    public void TheNewerCopyOfEachQuoteWinsAndDeletionsTravel()
    {
        var here = Quotes();
        var there = Quotes("there.json");

        var quote = here.Add("Hunter Builders", "door hardware", 420_000, Today);
        there.Merge(here.All);

        // The other computer wins it a minute later; this one does nothing.
        _now = _now.AddMinutes(1);
        there.Decide(quote.Id, QuoteStatus.Won, Today);

        here.Merge(there.All).ShouldBeTrue();
        here.Find(quote.Id)!.Status.ShouldBe(QuoteStatus.Won);

        _now = _now.AddMinutes(1);
        there.Delete(quote.Id);
        here.Merge(there.All).ShouldBeTrue();
        here.Find(quote.Id).ShouldBeNull();
        here.All.ShouldHaveSingleItem().Deleted.ShouldBeTrue();
    }

    [Fact]
    public void TwoComputersThatAgreeProduceTheSameSyncFile()
    {
        var here = Quotes();
        var there = Quotes("there.json");

        here.Add("B customer", "job", 100_000, Today);
        here.Add("A customer", "job", 200_000, Today);
        there.Merge(here.All);

        there.ToSyncJson().ShouldBe(here.ToSyncJson());
    }

    // ---- the chasing ----

    [Fact]
    public void AddingAQuoteBooksTheFirstChaseAsATask()
    {
        var quotes = Quotes();
        var tasks = Tasks();
        var chasing = new QuoteChasing(quotes, tasks, () => _now);

        var quote = quotes.Add("Hunter Builders", "door hardware", 420_000, Today);
        chasing.Follow().ShouldBe(1);

        var chase = tasks.Visible.ShouldHaveSingleItem();
        chase.Title.ShouldBe("Chase Hunter Builders — door hardware");
        chase.Category.ShouldBe("Quotes");
        chase.QuoteId.ShouldBe(quote.Id);
        chase.Due.ShouldBe(TaskPlan.Workday(Today.AddDays(3)));
        chase.Remind.ShouldNotBeNull();
        quotes.Find(quote.Id)!.ChaseTaskId.ShouldBe(chase.Id);
    }

    [Fact]
    public void TickingOffTheChaseCountsItAndBooksTheNextOne()
    {
        var quotes = Quotes();
        var tasks = Tasks();
        var chasing = new QuoteChasing(quotes, tasks, () => _now);

        var quote = quotes.Add("Hunter Builders", "door hardware", 420_000, Today);
        chasing.Follow();

        _now = _now.AddDays(3);
        tasks.Close(quotes.Find(quote.Id)!.ChaseTaskId!);
        chasing.Follow().ShouldBe(1);

        var moved = quotes.Find(quote.Id)!;
        moved.Chased.ShouldBe(1);
        moved.LastChased.ShouldBe(Today.AddDays(3));

        var next = tasks.Visible.Single(t => t.IsOpen);
        next.Title.ShouldBe("Chase Hunter Builders again — door hardware");
        next.Due.ShouldBe(TaskPlan.Workday(Today.AddDays(7)));
        moved.ChaseTaskId.ShouldBe(next.Id);
    }

    [Fact]
    public void WinningItStopsTheChasing()
    {
        var quotes = Quotes();
        var tasks = Tasks();
        var chasing = new QuoteChasing(quotes, tasks, () => _now);

        var quote = quotes.Add("Orikan", "readers", 900_000, Today);
        chasing.Follow();

        _now = _now.AddMinutes(1);
        quotes.Decide(quote.Id, QuoteStatus.Won, Today);
        chasing.Follow().ShouldBe(0);

        tasks.Visible.ShouldAllBe(t => !t.IsOpen);
        quotes.Find(quote.Id)!.ChaseTaskId.ShouldBeNull();
    }

    [Fact]
    public void AQuoteEnteredLateIsChasedTodayRatherThanOnADayAlreadyGone()
    {
        var quotes = Quotes();
        var tasks = Tasks();
        var chasing = new QuoteChasing(quotes, tasks, () => _now);

        quotes.Add("Hunter Builders", "door hardware", 420_000, Today.AddDays(-30));
        chasing.Follow();

        tasks.Visible.ShouldHaveSingleItem().Due.ShouldBe(Today);
    }

    [Fact]
    public void ALateQuotesChaseStaysOnTodayRatherThanBeingPutBack()
    {
        var quotes = Quotes();
        var tasks = Tasks();
        var chasing = new QuoteChasing(quotes, tasks, () => _now);

        quotes.Add("Hunter Builders", "door hardware", 420_000, Today.AddDays(-30));
        chasing.Follow();
        chasing.Follow();   // the re-timing pass must not drag it back to a day long gone

        tasks.Visible.ShouldHaveSingleItem().Due.ShouldBe(Today);
    }

    [Fact]
    public void DeletingAQuoteStopsItAskingToBeChased()
    {
        var quotes = Quotes();
        var tasks = Tasks();
        var chasing = new QuoteChasing(quotes, tasks, () => _now);

        var quote = quotes.Add("Hunter Builders", "door hardware", 420_000, Today);
        chasing.Follow();

        _now = _now.AddMinutes(1);
        quotes.Delete(quote.Id);
        chasing.Follow();

        tasks.Visible.ShouldAllBe(t => !t.IsOpen);
    }

    [Fact]
    public void FollowingTwiceChangesNothingTheSecondTime()
    {
        var quotes = Quotes();
        var tasks = Tasks();
        var chasing = new QuoteChasing(quotes, tasks, () => _now);

        quotes.Add("Hunter Builders", "door hardware", 420_000, Today);
        chasing.Follow().ShouldBe(1);
        chasing.Follow().ShouldBe(0);

        tasks.Visible.Count.ShouldBe(1);
    }

    [Fact]
    public void AChaseDeletedByHandIsBookedAgain()
    {
        var quotes = Quotes();
        var tasks = Tasks();
        var chasing = new QuoteChasing(quotes, tasks, () => _now);

        var quote = quotes.Add("Hunter Builders", "door hardware", 420_000, Today);
        chasing.Follow();

        tasks.Delete(quotes.Find(quote.Id)!.ChaseTaskId!);
        chasing.Follow().ShouldBe(1);

        tasks.Visible.ShouldHaveSingleItem().QuoteId.ShouldBe(quote.Id);
    }
}
