using System.Net;
using Shouldly;
using Teezy.Connectors;
using Teezy.Core.Calendar;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Reading a calendar published as an ICS link, in the shape Outlook publishes it.</summary>
public class IcsCalendarTests
{
    // Outlook's own conventions: Windows time-zone names with VTIMEZONE blocks, a weekly repeat
    // with one occurrence deleted (EXDATE) and one moved (RECURRENCE-ID), all-day leave as dates,
    // a meeting in Sydney across the start of daylight saving, and a cancelled meeting.
    private const string Feed = """
        BEGIN:VCALENDAR
        METHOD:PUBLISH
        PRODID:Microsoft Exchange Server 2010
        VERSION:2.0
        BEGIN:VTIMEZONE
        TZID:E. Australia Standard Time
        BEGIN:STANDARD
        DTSTART:16010101T000000
        TZOFFSETFROM:+1000
        TZOFFSETTO:+1000
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:16010101T000000
        TZOFFSETFROM:+1000
        TZOFFSETTO:+1000
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VTIMEZONE
        TZID:AUS Eastern Standard Time
        BEGIN:STANDARD
        DTSTART:16010101T030000
        TZOFFSETFROM:+1100
        TZOFFSETTO:+1000
        RRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=1SU;BYMONTH=4
        END:STANDARD
        BEGIN:DAYLIGHT
        DTSTART:16010101T020000
        TZOFFSETFROM:+1000
        TZOFFSETTO:+1100
        RRULE:FREQ=YEARLY;INTERVAL=1;BYDAY=1SU;BYMONTH=10
        END:DAYLIGHT
        END:VTIMEZONE
        BEGIN:VEVENT
        UID:standup-1
        SUMMARY:Team stand-up
        DTSTART;TZID=E. Australia Standard Time:20260803T090000
        DTEND;TZID=E. Australia Standard Time:20260803T091500
        RRULE:FREQ=WEEKLY;UNTIL=20261231T230000Z;INTERVAL=1;BYDAY=MO,WE,FR;WKST=SU
        EXDATE;TZID=E. Australia Standard Time:20260916T090000
        LOCATION:Microsoft Teams Meeting
        STATUS:CONFIRMED
        END:VEVENT
        BEGIN:VEVENT
        UID:standup-1
        RECURRENCE-ID;TZID=E. Australia Standard Time:20260918T090000
        SUMMARY:Team stand-up (moved)
        DTSTART;TZID=E. Australia Standard Time:20260918T100000
        DTEND;TZID=E. Australia Standard Time:20260918T101500
        END:VEVENT
        BEGIN:VEVENT
        UID:sydney-review
        SUMMARY:Quarterly review with Sydney
        DTSTART;TZID=AUS Eastern Standard Time:20261014T140000
        DTEND;TZID=AUS Eastern Standard Time:20261014T150000
        LOCATION:Level 3\, Board Room
        END:VEVENT
        BEGIN:VEVENT
        UID:leave
        SUMMARY:Annual leave
        DTSTART;VALUE=DATE:20260915
        DTEND;VALUE=DATE:20260917
        END:VEVENT
        BEGIN:VEVENT
        UID:cancelled
        SUMMARY:Budget sync
        STATUS:CANCELLED
        DTSTART:20260917T010000Z
        DTEND:20260917T020000Z
        END:VEVENT
        BEGIN:VEVENT
        UID:cancelled-by-organiser
        SUMMARY:Canceled: Supplier call
        DTSTART:20260917T030000Z
        DTEND:20260917T040000Z
        END:VEVENT
        END:VCALENDAR
        """;

    private static readonly TimeSpan Brisbane = TimeSpan.FromHours(10);

    private static IReadOnlyList<CalendarEvent> Between(DateTimeOffset from, DateTimeOffset to) =>
        IcsCalendar.Between(IcsCalendar.Parse(Feed), from, to);

    private static DateTimeOffset Utc(int month, int day, int hour, int minute = 0) =>
        new(2026, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void A_repeating_meeting_is_expanded_with_its_deleted_and_moved_occurrences_honoured()
    {
        var week = Between(new(2026, 9, 13, 0, 0, 0, Brisbane), new(2026, 9, 20, 0, 0, 0, Brisbane));

        var standUps = week.Where(e => e.Subject.StartsWith("Team stand-up", StringComparison.Ordinal)).ToList();

        standUps.Count.ShouldBe(2);
        standUps[0].Start.ShouldBe(Utc(9, 13, 23));
        standUps[0].Location.ShouldBe("Microsoft Teams Meeting");
        standUps[1].Subject.ShouldBe("Team stand-up (moved)");
        standUps[1].Start.ShouldBe(Utc(9, 18, 0));
    }

    [Fact]
    public void Cancelled_meetings_are_left_out_whichever_way_Outlook_marks_them()
    {
        var week = Between(new(2026, 9, 13, 0, 0, 0, Brisbane), new(2026, 9, 20, 0, 0, 0, Brisbane));

        week.ShouldNotContain(e => e.Subject.Contains("Budget sync"));
        week.ShouldNotContain(e => e.Subject.Contains("Supplier call"));
    }

    [Fact]
    public void All_day_leave_is_a_run_of_dates_anchored_to_local_midnight()
    {
        var leave = Between(new(2026, 9, 13, 0, 0, 0, Brisbane), new(2026, 9, 20, 0, 0, 0, Brisbane))
            .Single(e => e.Subject == "Annual leave");

        leave.IsAllDay.ShouldBeTrue();
        leave.Start.ShouldBe(CalendarWeek.Midnight(new DateOnly(2026, 9, 15)));
        leave.End.ShouldBe(CalendarWeek.Midnight(new DateOnly(2026, 9, 17)));
        leave.Source.ShouldBe(CalendarSource.Ics);
    }

    [Fact]
    public void Leave_that_began_before_the_question_is_still_part_of_the_answer()
    {
        var wednesday = new DateTimeOffset(2026, 9, 16, 12, 0, 0, Brisbane);

        Between(wednesday, wednesday.AddHours(6)).ShouldContain(e => e.Subject == "Annual leave");
    }

    [Fact]
    public void A_meeting_in_another_zone_lands_at_the_right_instant_across_daylight_saving()
    {
        // 2pm in Sydney on 14 October is AEDT, +11: 3am UTC.
        var review = Between(new(2026, 10, 14, 0, 0, 0, Brisbane), new(2026, 10, 15, 0, 0, 0, Brisbane))
            .Single(e => e.Subject == "Quarterly review with Sydney");

        review.Start.ShouldBe(Utc(10, 14, 3));
        review.Location.ShouldBe("Level 3, Board Room");
    }

    [Theory]
    [InlineData("webcal://outlook.office365.com/owa/calendar/abc/reachcalendar.ics", "https://outlook.office365.com/owa/calendar/abc/reachcalendar.ics")]
    [InlineData("  https://outlook.office365.com/owa/calendar/abc/calendar.ics ", "https://outlook.office365.com/owa/calendar/abc/calendar.ics")]
    [InlineData("http://example.com/calendar.ics", null)]
    [InlineData("not a link", null)]
    [InlineData("", null)]
    public void Links_are_taken_as_https_or_not_at_all(string pasted, string? expected)
    {
        IcsCalendar.Normalise(pasted).ShouldBe(expected);
    }

    [Fact]
    public void The_html_link_pasted_by_mistake_is_named_for_what_it_is()
    {
        var problem = Should.Throw<CalendarUnavailableException>(() =>
            IcsCalendar.Parse("<!DOCTYPE html><html><body>Calendar</body></html>"));

        problem.Message.ShouldContain("ICS link, not the HTML one");
    }

    [Fact]
    public async Task The_feed_is_fetched_once_and_reused_while_it_is_fresh()
    {
        var server = new FakeServer(HttpStatusCode.OK, Feed);
        var now = new DateTimeOffset(2026, 9, 13, 9, 0, 0, Brisbane);
        var calendar = new IcsCalendar(() => "https://example.com/calendar.ics", new HttpClient(server), () => now);

        await calendar.BetweenAsync(now, now.AddDays(7));
        await calendar.BetweenAsync(now, now.AddDays(1));
        server.Requests.ShouldBe(1);

        now = now.AddMinutes(11);
        await calendar.BetweenAsync(now, now.AddDays(1));
        server.Requests.ShouldBe(2);
    }

    [Fact]
    public async Task An_unpublished_calendar_says_it_needs_a_new_link()
    {
        var calendar = new IcsCalendar(
            () => "https://example.com/calendar.ics", new HttpClient(new FakeServer(HttpStatusCode.NotFound, "")));

        var problem = await Should.ThrowAsync<CalendarUnavailableException>(() =>
            calendar.BetweenAsync(DateTimeOffset.Now, DateTimeOffset.Now.AddDays(1)));

        problem.NeedsReconnect.ShouldBeTrue();
    }

    [Fact]
    public void With_no_link_saved_it_is_not_connected()
    {
        new IcsCalendar(() => null).IsConnected.ShouldBeFalse();
        new IcsCalendar(() => "http://insecure.example.com/cal.ics").IsConnected.ShouldBeFalse();
    }

    private sealed class FakeServer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
