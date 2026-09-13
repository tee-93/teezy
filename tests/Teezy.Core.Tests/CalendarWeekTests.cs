using Shouldly;
using Teezy.Core.Calendar;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Cutting a diary into Sunday-to-Saturday days.</summary>
public class CalendarWeekTests
{
    private static readonly TimeZoneInfo Brisbane =
        TimeZoneInfo.CreateCustomTimeZone("Test/Brisbane", TimeSpan.FromHours(10), "Brisbane", "Brisbane");

    // September 2026: Sunday the 13th to Saturday the 19th.
    private static DateTimeOffset At(int day, int hour, int minute = 0) =>
        new(2026, 9, day, hour, minute, 0, TimeSpan.FromHours(10));

    private static CalendarEvent Timed(string what, DateTimeOffset start, double hours = 1) =>
        new(what, start, start.AddHours(hours), false, null, CalendarSource.Microsoft);

    private static CalendarEvent AllDay(string what, int firstDay, int days = 1) =>
        new(what, At(firstDay, 0), At(firstDay + days, 0), true, null, CalendarSource.Google);

    private static IReadOnlyList<WeekDay> Week(DateTimeOffset now, params CalendarEvent[] events) =>
        CalendarWeek.Days(events, now, Brisbane);

    [Fact]
    public void The_week_runs_from_Sunday_midnight_to_the_next_Sunday()
    {
        CalendarWeek.Bounds(At(16, 14), Brisbane).ShouldBe((At(13, 0), At(20, 0)));
    }

    [Fact]
    public void On_a_Sunday_the_week_starts_that_morning()
    {
        CalendarWeek.Bounds(At(13, 7), Brisbane).From.ShouldBe(At(13, 0));
    }

    [Fact]
    public void Late_on_Saturday_it_is_still_the_same_week()
    {
        CalendarWeek.Bounds(At(19, 23, 30), Brisbane).From.ShouldBe(At(13, 0));
    }

    [Fact]
    public void A_week_across_the_end_of_a_month_starts_in_the_month_before()
    {
        var thursday = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.FromHours(10));

        CalendarWeek.Bounds(thursday, Brisbane).From.ShouldBe(At(27, 0));
    }

    [Fact]
    public void Every_day_is_there_in_order_with_today_and_the_past_marked()
    {
        var days = Week(At(16, 9));

        days.Count.ShouldBe(7);
        days[0].Date.ShouldBe(new DateOnly(2026, 9, 13));
        days[0].Date.DayOfWeek.ShouldBe(DayOfWeek.Sunday);
        days[6].Date.DayOfWeek.ShouldBe(DayOfWeek.Saturday);
        days[3].IsToday.ShouldBeTrue();
        days[2].IsPast.ShouldBeTrue();
        days[3].IsPast.ShouldBeFalse();
        days[4].IsPast.ShouldBeFalse();
    }

    [Fact]
    public void A_meeting_is_only_on_its_own_day()
    {
        var days = Week(At(16, 9), Timed("Review", At(15, 10)));

        days.Count(d => d.Events.Count > 0).ShouldBe(1);
        days[2].Events.ShouldHaveSingleItem().Subject.ShouldBe("Review");
    }

    [Fact]
    public void Something_all_day_over_several_days_is_on_each_of_them_and_not_the_day_after()
    {
        var days = Week(At(16, 9), AllDay("Leave", firstDay: 14, days: 2));

        days[1].Events.ShouldNotBeEmpty();
        days[2].Events.ShouldNotBeEmpty();
        days[3].Events.ShouldBeEmpty();
    }

    [Fact]
    public void A_meeting_that_ends_at_midnight_does_not_spill_into_the_next_day()
    {
        var days = Week(At(16, 9), Timed("Late call", At(16, 23)));

        days[3].Events.ShouldNotBeEmpty();
        days[4].Events.ShouldBeEmpty();
    }

    [Fact]
    public void Something_that_began_last_week_is_still_on_Sunday()
    {
        var days = Week(At(16, 9), AllDay("Trip", firstDay: 12, days: 2));

        days[0].Events.ShouldHaveSingleItem().Subject.ShouldBe("Trip");
    }

    [Fact]
    public void All_day_comes_before_timed_on_the_same_day()
    {
        var day = Week(At(16, 9), Timed("Stand-up", At(17, 8)), AllDay("Birthday", 17))[4];

        day.Events[0].Subject.ShouldBe("Birthday");
        day.Events[1].Subject.ShouldBe("Stand-up");
    }

    [Fact]
    public void A_zero_length_reminder_still_shows()
    {
        var reminder = new CalendarEvent("Pay rego", At(17, 9), At(17, 9), false, null, CalendarSource.Google);

        Week(At(16, 9), reminder)[4].Events.ShouldHaveSingleItem();
    }

    [Fact]
    public void Today_is_cut_from_the_same_reading_as_the_week()
    {
        var events = new[] { Timed("Stand-up", At(16, 9)), Timed("Dentist", At(18, 15)) };

        CalendarWeek.On(events, new DateOnly(2026, 9, 16), Brisbane)
            .ShouldHaveSingleItem().Subject.ShouldBe("Stand-up");
    }
}
