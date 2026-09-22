using Shouldly;
using Teezy.Core.Calendar;
using Teezy.Core.History;
using Teezy.Core.Home;
using Teezy.Core.Tasks;
using Xunit;

namespace Teezy.Core.Tests;

public class HomeTests
{
    // Tuesday 22 September 2026, 9:00 am, Sydney time.
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 9, 0, 0, TimeSpan.FromHours(10));
    private static readonly DateOnly Today = new(2026, 9, 22);

    private static TaskItem Task(string title, DateOnly? due = null, string? followUpOf = null,
        DateTimeOffset? remind = null, DateTimeOffset? closed = null) =>
        TaskItem.New(title, Now.AddDays(-10), due: due, followUpOf: followUpOf, remind: remind) with { Closed = closed };

    private static HomeSnapshot Snapshot(params TaskItem[] tasks) => new(Now, tasks, UsageStats.Empty, []);

    // ---- layout ----

    [Fact]
    public void AnEmptyChoiceMeansTheDefaults() =>
        HomeLayout.Sanitise([], HomeLayout.Tiles, HomeLayout.DefaultTiles, false, false, HomeLayout.MaxTiles)
            .ShouldBe(HomeLayout.DefaultTiles);

    [Fact]
    public void UnknownAndRepeatedKeysGoAndTilesAreCapped() =>
        HomeLayout.Sanitise(["streak", "nonsense", "streak", "overdue", "due_today", "time_saved", "done_week", "follow_ups"],
                HomeLayout.Tiles, HomeLayout.DefaultTiles, false, false, HomeLayout.MaxTiles)
            .ShouldBe(["streak", "overdue", "due_today", "time_saved", "done_week"]);

    [Fact]
    public void AccountPartsShowOnlyWhereTheAccountIs()
    {
        HomeLayout.Sanitise(["coming_up", "inbox"], HomeLayout.RightPanels, HomeLayout.DefaultRight, calendar: false, mail: false)
            .ShouldBe(["coming_up"]);
        HomeLayout.Sanitise(["coming_up", "inbox"], HomeLayout.RightPanels, HomeLayout.DefaultRight, calendar: false, mail: true)
            .ShouldBe(["coming_up", "inbox"]);
    }

    [Fact]
    public void EditingOnTheWorkLaptopKeepsThePersonalLaptopsInbox() =>
        HomeLayout.Merge(["notes", "coming_up"], ["coming_up", "inbox", "notes"], HomeLayout.RightPanels, calendar: false, mail: false)
            .ShouldBe(["notes", "coming_up", "inbox"]);

    [Fact]
    public void EveryDefaultWorksWithoutAnAccount()
    {
        HomeLayout.DefaultTiles.ShouldAllBe(k => HomeLayout.Find(k)!.IsAvailable(false, false));
        HomeLayout.DefaultLeft.ShouldAllBe(k => HomeLayout.Find(k)!.IsAvailable(false, false));
    }

    // ---- tiles ----

    [Fact]
    public void DueTodayCountsLateTasksAndWarns()
    {
        var tile = HomeTiles.Compute("due_today", Snapshot(
            Task("Late quote", Today.AddDays(-2)),
            Task("Today's call", Today),
            Task("Friday", Today.AddDays(3))));

        tile.Value.ShouldBe("2");
        tile.Caption.ShouldBe("1 late");
        tile.Tone.ShouldBe(TileTone.Warning);
    }

    [Fact]
    public void FollowUpsAreChainTasksDueThisWeek()
    {
        var first = Task("Send quote", closed: Now.AddDays(-3));
        var tile = HomeTiles.Compute("follow_ups", Snapshot(
            first,
            Task("Follow up: Send quote", Today.AddDays(2), followUpOf: first.Id),
            Task("Not a follow-up", Today.AddDays(1)),
            Task("Follow up later", Today.AddDays(20), followUpOf: first.Id)));

        tile.Value.ShouldBe("1");
        tile.Caption.ShouldStartWith("Next: Follow up: Send quote");
    }

    [Fact]
    public void NextReminderIsTheSoonestAheadAndOpensIt()
    {
        var soon = Task("Call Sam", remind: Now.AddHours(5));
        var tile = HomeTiles.Compute("next_reminder", Snapshot(
            Task("Past", remind: Now.AddHours(-1)),
            soon,
            Task("Later", remind: Now.AddDays(2))));

        tile.Value.ShouldBe("2pm");
        tile.Caption.ShouldBe("Call Sam");
        tile.Target.ShouldBe(TileTarget.Task);
        tile.TaskId.ShouldBe(soon.Id);
    }

    [Fact]
    public void DoneThisWeekCountsSinceMonday()
    {
        var closedMonday = Task("Monday's", closed: new DateTimeOffset(2026, 9, 21, 15, 0, 0, TimeSpan.FromHours(10)));
        var tile = HomeTiles.Compute("done_week", Snapshot(
            closedMonday,
            Task("Last week", closed: new DateTimeOffset(2026, 9, 18, 15, 0, 0, TimeSpan.FromHours(10))),
            Task("Follow up: Monday's", Today.AddDays(3), followUpOf: closedMonday.Id)));

        tile.Value.ShouldBe("1");
        tile.Caption.ShouldBe("1 followed up");
    }

    [Fact]
    public void AccountTilesSayWhenNothingIsConnected() =>
        HomeTiles.Compute("next_meeting", Snapshot()).Caption.ShouldBe("No calendar connected");

    // ---- the day's line ----

    [Fact]
    public void TheBriefLeadsWithTasks() =>
        DayBrief.For(Snapshot(Task("a", Today), Task("b", Today.AddDays(-1))))
            .ShouldBe("2 tasks today, 1 late.");

    [Fact]
    public void AClearDayIsNeverBlank() =>
        DayBrief.For(Snapshot()).ShouldStartWith("A clear day");

    [Fact]
    public void MeetingsJoinWhenACalendarIsConnected()
    {
        var at10 = new CalendarEvent("Site walk", Now.AddHours(1), Now.AddHours(2), false, null, CalendarSource.Microsoft);
        var brief = DayBrief.For(Snapshot(Task("a", Today)) with { Today = [at10] });

        brief.ShouldBe("1 task today · a meeting, the next at 10am.");
    }

    [Fact]
    public void GreetsByTheTimeOfDay()
    {
        DayBrief.Greeting(Now).ShouldBe("Good morning");
        DayBrief.Greeting(Now.AddHours(6)).ShouldBe("Good afternoon");
    }
}
