namespace Teezy.Core.Calendar;

/// <summary>One day of a week, with what is on it.</summary>
/// <param name="Date">The day, in local time.</param>
/// <param name="Events">Everything that touches the day — all-day first, then by start.</param>
/// <param name="IsToday">Whether it is today.</param>
/// <param name="IsPast">Whether it has already finished.</param>
public sealed record WeekDay(DateOnly Date, IReadOnlyList<CalendarEvent> Events, bool IsToday, bool IsPast);

/// <summary>A diary cut into days: today, and the week around it.</summary>
/// <remarks>
/// <para>
/// <b>A week runs Sunday to Saturday</b>, because that is how Zack counts one. It is a choice,
/// not a locale default — Australian settings would say Monday — so it lives here, once.
/// </para>
/// <para>
/// <b>Days are cut at local midnight, and an event belongs to every day it touches.</b> A
/// conference from Monday to Wednesday is on all three; a meeting that ends at midnight is not
/// on the next day. Both connectors already anchor all-day events to local midnight rather than
/// UTC, which is what makes this safe — a birthday read as UTC midnight would land on two days
/// east of Greenwich.
/// </para>
/// <para>
/// The zone is a parameter so tests can pin one. The app always passes nothing, meaning the
/// machine's own.
/// </para>
/// </remarks>
public static class CalendarWeek
{
    /// <summary>Sunday midnight to the following Sunday midnight, around <paramref name="now"/>.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Bounds(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var sunday = Sunday(Today(now, zone));
        return (Midnight(sunday, zone), Midnight(sunday.AddDays(7), zone));
    }

    /// <summary>Sunday to Saturday, every day present whether or not anything is on it.</summary>
    public static IReadOnlyList<WeekDay> Days(
        IReadOnlyList<CalendarEvent> events, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var today = Today(now, zone);
        var sunday = Sunday(today);

        return
        [
            .. Enumerable.Range(0, 7)
                .Select(sunday.AddDays)
                .Select(day => new WeekDay(day, On(events, day, zone), day == today, day < today)),
        ];
    }

    /// <summary>Everything that touches <paramref name="day"/>, all-day first, then by start.</summary>
    public static IReadOnlyList<CalendarEvent> On(
        IEnumerable<CalendarEvent> events, DateOnly day, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var start = Midnight(day, zone);
        var end = Midnight(day.AddDays(1), zone);

        return
        [
            .. events
                .Where(e => Touches(e, start, end))
                .OrderByDescending(e => e.IsAllDay)
                .ThenBy(e => e.Start),
        ];
    }

    /// <summary>The local date at <paramref name="now"/>.</summary>
    public static DateOnly Today(DateTimeOffset now, TimeZoneInfo? zone = null) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone ?? TimeZoneInfo.Local).DateTime);

    /// <summary>The instant a local day begins.</summary>
    public static DateTimeOffset Midnight(DateOnly day, TimeZoneInfo? zone = null)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, (zone ?? TimeZoneInfo.Local).GetUtcOffset(local));
    }

    private static DateOnly Sunday(DateOnly day) => day.AddDays(-(int)day.DayOfWeek);

    /// <summary>Whether an event overlaps a day, counting a zero-length reminder as an instant.</summary>
    private static bool Touches(CalendarEvent occurrence, DateTimeOffset start, DateTimeOffset end) =>
        occurrence.End > occurrence.Start
            ? occurrence.Start < end && occurrence.End > start
            : occurrence.Start >= start && occurrence.Start < end;
}
