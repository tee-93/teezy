using Shouldly;
using Teezy.Connectors;
using Teezy.Core.Calendar;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>
/// Reading Google's reply, which differs from Graph's in every way that matters.
/// </summary>
/// <remarks>
/// Google attaches the offset to the timestamp, marks all-day events by using a "date" field
/// instead of "dateTime", and leaves cancelled occurrences of a recurring event in the list as
/// tombstones. Each of those is a different bug if it is read as though it were Graph.
/// </remarks>
public class GoogleCalendarTests
{
    private static string Body(string items) => $$"""
        { "items": [ {{items}} ] }
        """;

    private const string Standup = """
        {
          "summary": "Stand-up",
          "status": "confirmed",
          "location": "Meet",
          "start": { "dateTime": "2026-09-14T08:30:00Z" },
          "end":   { "dateTime": "2026-09-14T08:45:00Z" }
        }
        """;

    [Fact]
    public void ReadsAMeeting()
    {
        var events = GoogleCalendar.Read(Body(Standup));

        events.Count.ShouldBe(1);
        events[0].Subject.ShouldBe("Stand-up");
        events[0].Location.ShouldBe("Meet");
        events[0].Source.ShouldBe(CalendarSource.Google);
        events[0].Duration.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void TheOffsetOnTheTimestampIsTrusted()
    {
        const string offset = """
            {
              "summary": "Review",
              "start": { "dateTime": "2026-09-14T15:00:00+10:00" },
              "end":   { "dateTime": "2026-09-14T16:00:00+10:00" }
            }
            """;

        // Unlike Graph, Google writes the offset onto the value, so there is no sibling
        // timezone field to consult and none to misread.
        GoogleCalendar.Read(Body(offset))[0].Start.ToUniversalTime()
            .ShouldBe(new DateTimeOffset(2026, 9, 14, 5, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void AnAllDayEventIsRecognisedByItsDateField()
    {
        const string leave = """
            {
              "summary": "Leave",
              "start": { "date": "2026-09-14" },
              "end":   { "date": "2026-09-15" }
            }
            """;

        var events = GoogleCalendar.Read(Body(leave));

        // "date" rather than "dateTime" is the whole signal — clearer than Graph's separate
        // isAllDay flag, and it must not be converted from UTC or the day slips.
        events[0].IsAllDay.ShouldBeTrue();
        events[0].Start.Date.ShouldBe(new DateTime(2026, 9, 14));
        events[0].Start.TimeOfDay.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void ACancelledOccurrenceIsNotAMeeting()
    {
        const string tombstone = """
            {
              "status": "cancelled",
              "start": { "dateTime": "2026-09-14T08:30:00Z" },
              "end":   { "dateTime": "2026-09-14T08:45:00Z" }
            }
            """;

        // A deleted occurrence of a recurring event stays in the list. Reading it as a meeting
        // would put a cancelled stand-up back into the answer.
        GoogleCalendar.Read(Body(tombstone)).ShouldBeEmpty();
    }

    [Fact]
    public void EventsComeBackInOrder()
    {
        const string later = """
            {
              "summary": "Review",
              "start": { "dateTime": "2026-09-14T15:00:00Z" },
              "end":   { "dateTime": "2026-09-14T16:00:00Z" }
            }
            """;

        GoogleCalendar.Read(Body($"{later}, {Standup}"))
            .Select(e => e.Subject).ShouldBe(["Stand-up", "Review"]);
    }

    [Fact]
    public void AnEventWithNoTimesIsSkippedRatherThanInvented()
    {
        var events = GoogleCalendar.Read(Body($$"""{ "summary": "Mystery" }, {{Standup}}"""));

        events.Count.ShouldBe(1);
        events[0].Subject.ShouldBe("Stand-up");
    }

    [Fact]
    public void AnEmptyDiaryIsAnEmptyListNotAFailure() =>
        GoogleCalendar.Read("""{ "items": [] }""").ShouldBeEmpty();

    [Fact]
    public void NonsenseIsReportedAsUnavailable() =>
        Should.Throw<CalendarUnavailableException>(() => GoogleCalendar.Read("not json"));

    [Fact]
    public void TheGoogleProviderAsksForReadOnlyAccess()
    {
        var provider = GoogleCalendar.Provider("client-id", "secret");

        provider.Scopes.ShouldContain("https://www.googleapis.com/auth/calendar.readonly");
        provider.Scopes.ShouldNotContain(s => s.EndsWith("/calendar", StringComparison.Ordinal));

        // Gmail is deliberately absent: its read scope is restricted rather than sensitive, so
        // asking for it here would drag the whole client into Google's verification process.
        provider.Scopes.ShouldNotContain(s => s.Contains("gmail", StringComparison.Ordinal));

        // Google documents the loopback literal and has deprecated localhost for new clients —
        // the opposite of Microsoft, which is the reason the host is per-provider at all.
        provider.RedirectHost.ShouldBe("127.0.0.1");

        provider.ClientSecret.ShouldBe("secret");
    }
}
