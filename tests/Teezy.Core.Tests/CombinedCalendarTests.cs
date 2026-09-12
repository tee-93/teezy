using Shouldly;
using Teezy.Core.Calendar;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Reading several accounts as one diary, including when one of them will not.</summary>
public class CombinedCalendarTests
{
    private sealed class FakeCalendar(CalendarSource source) : ICalendar
    {
        public CalendarSource Source { get; } = source;
        public bool IsConnected { get; set; } = true;
        public List<CalendarEvent> Events { get; } = [];
        public string? FailWith { get; set; }
        public int Reads { get; private set; }

        public Task<IReadOnlyList<CalendarEvent>> BetweenAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            Reads++;

            if (FailWith is { } why) throw new CalendarUnavailableException(why);

            return Task.FromResult<IReadOnlyList<CalendarEvent>>(Events);
        }
    }

    private static readonly DateTimeOffset Monday =
        new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);

    private static CalendarEvent Meeting(string subject, int hour, CalendarSource source) =>
        new(subject, Monday.AddHours(hour), Monday.AddHours(hour + 1), false, null, source);

    private static Task<CalendarReading> Read(params ICalendar[] accounts) =>
        new CombinedCalendar(accounts).BetweenAsync(Monday, Monday.AddDays(1));

    [Fact]
    public async Task MergesTwoAccountsIntoOneOrderedDiary()
    {
        var work = new FakeCalendar(CalendarSource.Microsoft);
        work.Events.Add(Meeting("Review", 14, CalendarSource.Microsoft));

        var home = new FakeCalendar(CalendarSource.Google);
        home.Events.Add(Meeting("Dentist", 9, CalendarSource.Google));

        var reading = await Read(work, home);

        reading.Events.Select(e => e.Subject).ShouldBe(["Dentist", "Review"]);
        reading.Unavailable.ShouldBeEmpty();
    }

    [Fact]
    public async Task OneAccountFailingStillAnswersFromTheOther()
    {
        var work = new FakeCalendar(CalendarSource.Microsoft) { FailWith = "token expired" };

        var home = new FakeCalendar(CalendarSource.Google);
        home.Events.Add(Meeting("Dentist", 9, CalendarSource.Google));

        var reading = await Read(work, home);

        reading.Events.Count.ShouldBe(1);

        // And says which one it could not see, so the answer can admit it.
        reading.Unavailable.ShouldBe([CalendarSource.Microsoft]);
        reading.NothingAnswered.ShouldBeFalse();
    }

    [Fact]
    public async Task EverythingFailingIsNotAnEmptyDiary()
    {
        var work = new FakeCalendar(CalendarSource.Microsoft) { FailWith = "no" };
        var home = new FakeCalendar(CalendarSource.Google) { FailWith = "no" };

        var reading = await Read(work, home);

        // The distinction that stops "nothing on this afternoon" being said about a calendar
        // nobody could read.
        reading.NothingAnswered.ShouldBeTrue();
    }

    [Fact]
    public async Task DisconnectedAccountsAreNotAsked()
    {
        var never = new FakeCalendar(CalendarSource.Google) { IsConnected = false };

        var reading = await Read(never);

        never.Reads.ShouldBe(0);
        reading.Events.ShouldBeEmpty();
        reading.Unavailable.ShouldBeEmpty();
    }

    [Fact]
    public void NothingConnectedIsNotConnected()
    {
        var never = new FakeCalendar(CalendarSource.Google) { IsConnected = false };

        new CombinedCalendar([never]).IsConnected.ShouldBeFalse();
        new CombinedCalendar([]).IsConnected.ShouldBeFalse();
        new CombinedCalendar([new FakeCalendar(CalendarSource.Microsoft)]).IsConnected.ShouldBeTrue();
    }

    [Fact]
    public async Task TwoAccountsFromTheSameProviderAreBothRead()
    {
        // The ordinary case for anyone with a job, and the reason accounts are a list.
        var personal = new FakeCalendar(CalendarSource.Microsoft);
        personal.Events.Add(Meeting("Dentist", 9, CalendarSource.Microsoft));

        var work = new FakeCalendar(CalendarSource.Microsoft);
        work.Events.Add(Meeting("Review", 14, CalendarSource.Microsoft));

        var reading = await Read(personal, work);

        reading.Events.Select(e => e.Subject).ShouldBe(["Dentist", "Review"]);
    }

    [Fact]
    public async Task TheSameProviderFailingTwiceIsSaidOnce()
    {
        var personal = new FakeCalendar(CalendarSource.Microsoft) { FailWith = "no" };
        var work = new FakeCalendar(CalendarSource.Microsoft) { FailWith = "no" };

        (await Read(personal, work)).Unavailable.ShouldBe([CalendarSource.Microsoft]);
    }

    [Fact]
    public async Task AnAllDayEventLeadsTheDayItShares()
    {
        var account = new FakeCalendar(CalendarSource.Microsoft);
        account.Events.Add(new CalendarEvent(
            "Midnight deploy", Monday, Monday.AddHours(1), false, null, CalendarSource.Microsoft));
        account.Events.Add(new CalendarEvent(
            "Leave", Monday, Monday.AddDays(1), true, null, CalendarSource.Microsoft));

        var reading = await Read(account);

        reading.Events.Select(e => e.Subject).ShouldBe(["Leave", "Midnight deploy"]);
    }
}
