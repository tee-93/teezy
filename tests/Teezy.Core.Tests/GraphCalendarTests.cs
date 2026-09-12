using Shouldly;
using Teezy.Connectors;
using Teezy.Core.Calendar;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>
/// Reading Graph's reply, which is the half that can be tested without an account.
/// </summary>
/// <remarks>
/// The bodies here are the shapes Graph actually returns for a calendar asked in UTC — the
/// offset-less <c>dateTime</c>, the separate <c>timeZone</c>, the nested location. Getting any
/// of that wrong produces meetings at plausible but wrong times, which is the failure mode
/// least likely to be noticed and most likely to matter.
/// </remarks>
public class GraphCalendarTests
{
    private static string Body(string events) => $$"""
        { "value": [ {{events}} ] }
        """;

    private const string Standup = """
        {
          "subject": "Stand-up",
          "isAllDay": false,
          "start": { "dateTime": "2026-09-14T08:30:00.0000000", "timeZone": "UTC" },
          "end":   { "dateTime": "2026-09-14T08:45:00.0000000", "timeZone": "UTC" },
          "location": { "displayName": "Teams" }
        }
        """;

    [Fact]
    public void ReadsAMeeting()
    {
        var events = GraphCalendar.Read(Body(Standup));

        events.Count.ShouldBe(1);
        events[0].Subject.ShouldBe("Stand-up");
        events[0].Location.ShouldBe("Teams");
        events[0].Source.ShouldBe(CalendarSource.Microsoft);
        events[0].Duration.ShouldBe(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public void TimesAreReadAsUtcAndReturnedLocal()
    {
        var events = GraphCalendar.Read(Body(Standup));

        // Graph names the zone in a sibling field rather than on the value, so a parser that
        // trusts the string alone would read 08:30 UTC as 08:30 wherever the user is.
        events[0].Start.ToUniversalTime()
            .ShouldBe(new DateTimeOffset(2026, 9, 14, 8, 30, 0, TimeSpan.Zero));

        events[0].Start.Offset.ShouldBe(TimeZoneInfo.Local.GetUtcOffset(events[0].Start));
    }

    [Fact]
    public void AllDayEventsKeepTheirDate()
    {
        const string birthday = """
            {
              "subject": "Leave",
              "isAllDay": true,
              "start": { "dateTime": "2026-09-14T00:00:00.0000000", "timeZone": "UTC" },
              "end":   { "dateTime": "2026-09-15T00:00:00.0000000", "timeZone": "UTC" }
            }
            """;

        var events = GraphCalendar.Read(Body(birthday));

        // Converting an all-day event from UTC moves it to the previous evening anywhere west
        // of Greenwich, and answers "what's on today" with yesterday's leave.
        events[0].IsAllDay.ShouldBeTrue();
        events[0].Start.Date.ShouldBe(new DateTime(2026, 9, 14));
        events[0].Start.TimeOfDay.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void EventsComeBackInOrder()
    {
        const string later = """
            {
              "subject": "Review",
              "isAllDay": false,
              "start": { "dateTime": "2026-09-14T15:00:00.0000000", "timeZone": "UTC" },
              "end":   { "dateTime": "2026-09-14T16:00:00.0000000", "timeZone": "UTC" }
            }
            """;

        var events = GraphCalendar.Read(Body($"{later}, {Standup}"));

        events.Select(e => e.Subject).ShouldBe(["Stand-up", "Review"]);
    }

    [Fact]
    public void AMissingLocationIsNullRatherThanEmpty()
    {
        const string nowhere = """
            {
              "subject": "Think",
              "isAllDay": false,
              "start": { "dateTime": "2026-09-14T09:00:00.0000000", "timeZone": "UTC" },
              "end":   { "dateTime": "2026-09-14T10:00:00.0000000", "timeZone": "UTC" },
              "location": { "displayName": "" }
            }
            """;

        GraphCalendar.Read(Body(nowhere))[0].Location.ShouldBeNull();
    }

    [Fact]
    public void AnEmptyDiaryIsAnEmptyListNotAFailure()
    {
        GraphCalendar.Read("""{ "value": [] }""").ShouldBeEmpty();
    }

    [Fact]
    public void AnEventMissingItsTimesIsSkippedRatherThanInvented()
    {
        var events = GraphCalendar.Read(Body($$"""{ "subject": "Mystery" }, {{Standup}}"""));

        events.Count.ShouldBe(1);
        events[0].Subject.ShouldBe("Stand-up");
    }

    [Fact]
    public void NonsenseIsReportedAsUnavailable()
    {
        Should.Throw<CalendarUnavailableException>(() => GraphCalendar.Read("not json"));
    }

    [Fact]
    public void TheMicrosoftProviderAsksForReadOnlyAccess()
    {
        var provider = GraphCalendar.Provider("client-id");

        provider.Scopes.ShouldContain("Calendars.Read");
        provider.Scopes.ShouldNotContain(s => s.Contains("ReadWrite", StringComparison.Ordinal));

        // Without this the connection silently lasts one hour.
        provider.Scopes.ShouldContain("offline_access");

        // A public client has no secret to keep, and PKCE is what stands in for one.
        provider.ClientSecret.ShouldBeNull();

        // Microsoft ignores the port only for a registered http://localhost.
        provider.RedirectHost.ShouldBe("localhost");
    }
}
