using Shouldly;
using Teezy.Core.Tasks;
using Xunit;

namespace Teezy.Core.Tests;

public sealed class TaskStoreTests : IDisposable
{
    // Tuesday 22 September 2026.
    private static readonly DateOnly Today = new(2026, 9, 22);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "teezy-tasks-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 9, 22, 9, 0, 0, TimeSpan.FromHours(10));

    private string FilePath => Path.Combine(_folder, "tasks.json");

    private TaskStore Store() => new(FilePath, () => _now);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void KeepsTasksAcrossRestarts()
    {
        var store = Store();
        var added = store.Add("Chase Cessnock quote", "Quotes", due: Today.AddDays(3), dueTime: new TimeOnly(14, 0));
        store.AddNote(added.Id, "Left a voicemail");

        var again = Store().Find(added.Id)!;

        again.Title.ShouldBe("Chase Cessnock quote");
        again.Category.ShouldBe("Quotes");
        again.Due.ShouldBe(Today.AddDays(3));
        again.DueTime.ShouldBe(new TimeOnly(14, 0));
        again.Notes.Single().Text.ShouldBe("Left a voicemail");
    }

    [Fact]
    public void CloseAndFollowUpMakesALinkedTaskInTheSameCategory()
    {
        var store = Store();
        var quote = store.Add("Send Cessnock quote", "Quotes");

        var followUp = store.CloseAndFollowUp(quote.Id, Today.AddDays(7));

        store.Find(quote.Id)!.IsOpen.ShouldBeFalse();
        store.Find(quote.Id)!.Notes.Last().Text.ShouldContain("followed up");
        followUp.FollowUpOf.ShouldBe(quote.Id);
        followUp.Category.ShouldBe("Quotes");
        followUp.Title.ShouldBe("Follow up: Send Cessnock quote");
        followUp.Due.ShouldBe(Today.AddDays(7));
    }

    [Fact]
    public void UndoingAFollowUpLeavesTheTaskAsItWas()
    {
        var store = Store();
        var quote = store.Add("Send quote", "Quotes");
        store.AddNote(quote.Id, "Sent to Priya");
        var followUp = store.CloseAndFollowUp(quote.Id, Today.AddDays(3));

        store.UndoFollowUp(quote.Id, followUp.Id);

        store.Find(followUp.Id).ShouldBeNull();
        store.Find(quote.Id)!.IsOpen.ShouldBeTrue();
        store.Find(quote.Id)!.Notes.Select(n => n.Text).ShouldBe(["Sent to Priya"]);
    }

    [Fact]
    public void AChainReadsOldestFirstFromAnyTaskInIt()
    {
        var store = Store();
        var first = store.Add("Send quote");
        _now = _now.AddMinutes(1);
        var second = store.CloseAndFollowUp(first.Id, Today.AddDays(3));
        _now = _now.AddMinutes(1);
        var third = store.CloseAndFollowUp(second.Id, Today.AddDays(10));

        third.Title.ShouldBe("Follow up: Send quote");
        TaskPlan.Chain(store.Visible, second.Id).Select(t => t.Id).ShouldBe([first.Id, second.Id, third.Id]);
        TaskPlan.Chain(store.Visible, third.Id).Select(t => t.Id).ShouldBe([first.Id, second.Id, third.Id]);
    }

    [Fact]
    public void BucketsFollowStartThenDue()
    {
        TaskItem T(DateOnly? start, DateOnly? due) => TaskItem.New("x", _now, start: start, due: due);

        TaskPlan.BucketOf(T(null, Today.AddDays(-1)), Today).ShouldBe(TaskBucket.Overdue);
        TaskPlan.BucketOf(T(null, Today), Today).ShouldBe(TaskBucket.Today);
        TaskPlan.BucketOf(T(null, Today.AddDays(2)), Today).ShouldBe(TaskBucket.Upcoming);
        TaskPlan.BucketOf(T(null, null), Today).ShouldBe(TaskBucket.NoDate);
        TaskPlan.BucketOf(T(Today.AddDays(1), Today.AddDays(1)), Today).ShouldBe(TaskBucket.NotStarted);
        TaskPlan.BucketOf(T(Today, Today), Today).ShouldBe(TaskBucket.Today);
    }

    [Fact]
    public void DueTodayIsOverdueThenTodayAndLeavesClosedOut()
    {
        var store = Store();
        store.Add("today", due: Today);
        store.Add("late", due: Today.AddDays(-2));
        store.Add("later", due: Today.AddDays(1));
        var done = store.Add("done", due: Today);
        store.Close(done.Id);

        TaskPlan.DueToday(store.Visible, Today).Select(t => t.Title).ShouldBe(["late", "today"]);
    }

    [Fact]
    public void MergeTakesTheNewerCopyOfEachTask()
    {
        var here = Store();
        var shared = here.Add("Shared task");
        var mine = here.Add("Only here");

        // The other computer edited the shared task later, and added one of its own.
        var theirs = new[]
        {
            shared with { Title = "Shared task, renamed", Modified = _now.AddMinutes(5) },
            TaskItem.New("Only there", _now),
        };

        here.Merge(theirs).ShouldBeTrue();

        here.Find(shared.Id)!.Title.ShouldBe("Shared task, renamed");
        here.Find(mine.Id).ShouldNotBeNull();
        here.Visible.Select(t => t.Title).ShouldContain("Only there");
    }

    [Fact]
    public void MergeKeepsTheLocalEditWhenItIsNewer()
    {
        var here = Store();
        var task = here.Add("Original");
        var stale = task with { Title = "Old edit elsewhere", Modified = _now.AddMinutes(-5) };

        here.Merge([stale]).ShouldBeFalse();
        here.Find(task.Id)!.Title.ShouldBe("Original");
    }

    [Fact]
    public void ADeletionReachesTheOtherComputer()
    {
        var here = Store();
        var task = here.Add("Going away");
        var there = new TaskStore(Path.Combine(_folder, "there.json"), () => _now);
        there.Merge(here.All);

        _now = _now.AddMinutes(1);
        here.Delete(task.Id);
        there.Merge(here.All);

        there.Find(task.Id).ShouldBeNull();
        there.All.Single(t => t.Id == task.Id).Deleted.ShouldBeTrue();
    }

    [Fact]
    public void AnUnreadableFileIsKeptAside()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(FilePath, "{ not json");

        var store = Store();

        store.Visible.ShouldBeEmpty();
        File.Exists(FilePath + ".unreadable").ShouldBeTrue();
    }

    [Fact]
    public void RemindersComeDueOnceAndOnlyWithATime()
    {
        var store = Store();
        var timed = store.Add("Call Sam", due: Today, dueTime: new TimeOnly(14, 0));
        store.Add("No time", due: Today);
        store.Add("Later", due: Today, dueTime: new TimeOnly(16, 0));

        var at = Today.ToDateTime(new TimeOnly(14, 1));
        TaskPlan.DueForReminder(store.Visible, at).Select(t => t.Title).ShouldBe(["Call Sam"]);

        store.MarkReminded(timed.Id);
        TaskPlan.DueForReminder(store.Visible, at).ShouldBeEmpty();
    }

    [Fact]
    public void ShowingAReminderIsNotAChangeForSync()
    {
        var store = Store();
        var task = store.Add("Call Sam", due: Today, dueTime: new TimeOnly(14, 0));
        var before = store.ToSyncJson();

        _now = _now.AddMinutes(5);
        store.MarkReminded(task.Id);

        store.ToSyncJson().ShouldBe(before);
        store.Find(task.Id)!.Modified.ShouldBe(task.Modified);
    }

    [Fact]
    public void FollowUpsSkipTheWeekend()
    {
        TaskPlan.Workday(new DateOnly(2026, 9, 26)).ShouldBe(new DateOnly(2026, 9, 28));
        TaskPlan.Workday(new DateOnly(2026, 9, 27)).ShouldBe(new DateOnly(2026, 9, 28));
        TaskPlan.Workday(new DateOnly(2026, 9, 25)).ShouldBe(new DateOnly(2026, 9, 25));
    }

    [Fact]
    public void CategoriesAreDistinctAndSorted()
    {
        var store = Store();
        store.Add("a", "Quotes");
        store.Add("b", "quotes");
        store.Add("c", "Admin");
        store.Add("d");

        TaskPlan.Categories(store.Visible).ShouldBe(["Admin", "Quotes"]);
    }
}

public class TaskInputTests
{
    // Tuesday 22 September 2026.
    private static readonly DateOnly Today = new(2026, 9, 22);

    [Theory]
    [InlineData("Chase Cessnock quote fri", "Chase Cessnock quote", 2026, 9, 25)]
    [InlineData("Chase quote tomorrow", "Chase quote", 2026, 9, 23)]
    [InlineData("Chase quote by today", "Chase quote", 2026, 9, 22)]
    [InlineData("Chase quote in 3 days", "Chase quote", 2026, 9, 25)]
    [InlineData("Chase quote next week", "Chase quote", 2026, 9, 29)]
    [InlineData("Chase quote 1/10", "Chase quote", 2026, 10, 1)]
    [InlineData("Chase quote tue", "Chase quote", 2026, 9, 29)]
    [InlineData("Chase quote 3 Oct", "Chase quote", 2026, 10, 3)]
    public void ReadsADateAtTheEnd(string text, string title, int y, int m, int d)
    {
        var parsed = TaskInput.Parse(text, Today);

        parsed.Title.ShouldBe(title);
        parsed.Due.ShouldBe(new DateOnly(y, m, d));
    }

    [Fact]
    public void ReadsATimeAndACategory()
    {
        var parsed = TaskInput.Parse("Call Sam fri 2:30pm #Sales_Excellence", Today);

        parsed.Title.ShouldBe("Call Sam");
        parsed.Due.ShouldBe(new DateOnly(2026, 9, 25));
        parsed.DueTime.ShouldBe(new TimeOnly(14, 30));
        parsed.Category.ShouldBe("Sales Excellence");
    }

    [Fact]
    public void LeavesDateWordsInsideTheTitleAlone()
    {
        var parsed = TaskInput.Parse("Call Friday Electrical about the quote", Today);

        parsed.Title.ShouldBe("Call Friday Electrical about the quote");
        parsed.Due.ShouldBeNull();
    }

    [Fact]
    public void AOneWordTaskIsNeverEaten()
    {
        TaskInput.Parse("Tomorrow", Today).Title.ShouldBe("Tomorrow");
    }

    [Fact]
    public void ADayAlreadyPastMeansNextYear()
    {
        TaskInput.TryDate("1/2", Today, out var date).ShouldBeTrue();
        date.ShouldBe(new DateOnly(2027, 2, 1));
    }
}
