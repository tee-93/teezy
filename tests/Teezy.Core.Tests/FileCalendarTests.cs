using Shouldly;
using Teezy.Connectors;
using Teezy.Core.Calendar;
using Xunit;

namespace Teezy.Core.Tests;

public class FileCalendarTests
{
    // The shape "Get calendar view of events (V3)" returns, trimmed to the fields that matter.
    private const string View = """
        {
          "value": [
            {
              "subject": "Project review",
              "start": "2026-09-22T23:00:00.0000000",
              "end": "2026-09-23T00:00:00.0000000",
              "startWithTimeZone": "2026-09-22T23:00:00+00:00",
              "endWithTimeZone": "2026-09-23T00:00:00+00:00",
              "isAllDay": false,
              "location": "Level 3 boardroom",
              "showAs": "busy"
            },
            {
              "subject": "Canceled: Supplier call",
              "start": "2026-09-23T01:00:00.0000000",
              "end": "2026-09-23T01:30:00.0000000",
              "isAllDay": false
            },
            {
              "subject": "Annual leave",
              "start": "2026-09-25T00:00:00.0000000",
              "end": "2026-09-26T00:00:00.0000000",
              "isAllDay": true,
              "location": ""
            }
          ]
        }
        """;

    [Fact]
    public void ReadsMeetingsAtTheRightInstant()
    {
        var review = FileCalendar.Parse(View).Single(e => e.Subject == "Project review");

        review.Start.UtcDateTime.ShouldBe(new DateTime(2026, 9, 22, 23, 0, 0, DateTimeKind.Utc));
        review.End.UtcDateTime.ShouldBe(new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc));
        review.Location.ShouldBe("Level 3 boardroom");
        review.Source.ShouldBe(CalendarSource.File);
    }

    [Fact]
    public void CancelledMeetingsAreLeftOut() =>
        FileCalendar.Parse(View).ShouldNotContain(e => e.Subject.Contains("Supplier"));

    [Fact]
    public void AnAllDayEventKeepsItsDateWhateverTheTimeZone()
    {
        var leave = FileCalendar.Parse(View).Single(e => e.Subject == "Annual leave");

        leave.IsAllDay.ShouldBeTrue();
        leave.Start.Date.ShouldBe(new DateTime(2026, 9, 25));
        leave.Location.ShouldBeNull();
    }

    [Fact]
    public void PlainTimesWithoutAZoneAreReadAsUtc() =>
        // The flow's "start" field is UTC unless the flow was set otherwise.
        FileCalendar.Parse("""{"value":[{"subject":"x","start":"2026-09-22T01:00:00","end":"2026-09-22T02:00:00"}]}""")
            .Single().Start.UtcDateTime.Hour.ShouldBe(1);

    [Fact]
    public void ABareArrayIsAcceptedToo() =>
        FileCalendar.Parse("""[{"subject":"x","start":"2026-09-22T01:00:00"}]""").Count.ShouldBe(1);

    [Fact]
    public void SomethingElseIsRefusedWithAReason() =>
        Should.Throw<CalendarUnavailableException>(() => FileCalendar.Parse("""{"hello":"world"}"""))
            .Message.ShouldContain("Get calendar view of events");

    [Fact]
    public async Task OnlyEventsInTheRangeAreReturned()
    {
        var path = Path.Combine(Path.GetTempPath(), $"teezy-cal-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, View);
        try
        {
            var calendar = new FileCalendar(() => path);
            var from = new DateTimeOffset(2026, 9, 22, 22, 0, 0, TimeSpan.Zero);

            var events = await calendar.BetweenAsync(from, from.AddHours(3));

            events.Select(e => e.Subject).ShouldBe(["Project review"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
