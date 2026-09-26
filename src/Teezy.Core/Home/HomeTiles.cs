using System.Globalization;
using Teezy.Core.Calendar;
using Teezy.Core.History;
using Teezy.Core.Mail;
using Teezy.Core.Tasks;

namespace Teezy.Core.Home;

/// <summary>Everything Home's tiles and header line are worked out from, read once per refresh.</summary>
/// <param name="Tasks">Every task that is not deleted.</param>
/// <param name="Quotes">Every quote that is not deleted.</param>
/// <param name="Meetings">When each recorded meeting started, newest first.</param>
/// <param name="Today">Today's calendar events, or null when no calendar is connected.</param>
/// <param name="Mail">Recent mail, or null when no mailbox is connected.</param>
public sealed record HomeSnapshot(
    DateTimeOffset Now,
    IReadOnlyList<TaskItem> Tasks,
    UsageStats Usage,
    IReadOnlyList<DateTimeOffset> Meetings,
    IReadOnlyList<CalendarEvent>? Today = null,
    MailReading? Mail = null,
    IReadOnlyList<Quotes.Quote>? Quotes = null)
{
    public DateOnly Date => DateOnly.FromDateTime(Now.LocalDateTime);

    /// <summary>The quotes, never null.</summary>
    public IReadOnlyList<Quotes.Quote> AllQuotes => Quotes ?? [];

    /// <summary>Monday of this week: "this week" at work starts on Monday.</summary>
    public DateOnly WeekStart => Date.AddDays(-(((int)Date.DayOfWeek + 6) % 7));
}

public enum TileTone
{
    Neutral,
    Good,
    Warning,
    Bad,
}

/// <summary>Where clicking a tile goes.</summary>
public enum TileTarget
{
    None,
    Tasks,
    Task,
    Insights,
    Meetings,
    Quotes,
}

/// <summary>What one tile shows: a big value, a line under it, a colour, and where it leads.</summary>
public sealed record TileResult(string Value, string Caption, TileTone Tone = TileTone.Neutral,
    TileTarget Target = TileTarget.None, string? TaskId = null);

/// <summary>Works out each tile. Pure functions of a <see cref="HomeSnapshot"/>, so they are tested directly.</summary>
public static class HomeTiles
{
    private static readonly CultureInfo Display = CultureInfo.GetCultureInfo("en-AU");

    public static TileResult Compute(string key, HomeSnapshot s) => key switch
    {
        "quotes_open" => QuotesOut(s),
        "won_month" => WonThisMonth(s),
        "due_today" => DueToday(s),
        "follow_ups" => FollowUps(s),
        "next_reminder" => NextReminder(s),
        "done_week" => DoneThisWeek(s),
        "dictated_week" => DictatedThisWeek(s),
        "overdue" => Overdue(s),
        "time_saved" => TimeSaved(s),
        "streak" => Streak(s),
        "meetings_week" => MeetingsThisWeek(s),
        "next_meeting" => NextMeeting(s),
        "unread" => Unread(s),
        _ => new TileResult("—", string.Empty),
    };

    /// <summary>What is out there, and whether any of it wants chasing today.</summary>
    private static TileResult QuotesOut(HomeSnapshot s)
    {
        var totals = Quotes.QuotePlan.Totals(s.AllQuotes, s.Date);
        if (totals.Open.Count == 0) return new TileResult("—", "No quotes out", TileTone.Neutral, TileTarget.Quotes);

        var chase = Quotes.QuotePlan.DueToChase(s.AllQuotes, s.Date).Count;
        var quiet = s.AllQuotes.Count(q => Quotes.QuotePlan.IsQuiet(q, s.Date));

        var caption = chase > 0
            ? chase == 1 ? "1 to chase today" : $"{chase} to chase today"
            : quiet > 0
                ? quiet == 1 ? "1 gone quiet" : $"{quiet} gone quiet"
                : totals.Open.Count == 1 ? "1 quote open" : $"{totals.Open.Count} quotes open";

        return new TileResult(
            Quotes.QuotePlan.Money(totals.Open.Value),
            caption,
            chase > 0 ? TileTone.Warning : TileTone.Neutral,
            TileTarget.Quotes);
    }

    /// <summary>The month's wins, which is the figure worth seeing first thing.</summary>
    private static TileResult WonThisMonth(HomeSnapshot s)
    {
        var totals = Quotes.QuotePlan.Totals(s.AllQuotes, s.Date);
        if (totals.Won.Count == 0 && totals.Lost.Count == 0)
        {
            return new TileResult("—", "Nothing decided yet", TileTone.Neutral, TileTarget.Quotes);
        }

        var caption = totals.WinRate is { } rate
            ? $"{totals.Won.Count} of {totals.Won.Count + totals.Lost.Count} · {rate:P0}"
            : $"{totals.Won.Count} quotes";

        return new TileResult(
            Quotes.QuotePlan.Money(totals.Won.Value), caption, TileTone.Good, TileTarget.Quotes);
    }

    private static TileResult DueToday(HomeSnapshot s)
    {
        var due = TaskPlan.DueToday(s.Tasks, s.Date);
        var late = due.Count(t => t.Due < s.Date);
        var caption = due.Count == 0 ? "Nothing due" : late == 0 ? "All on time" : late == 1 ? "1 late" : $"{late} late";
        return new TileResult(Count(due.Count), caption, late > 0 ? TileTone.Warning : TileTone.Neutral, TileTarget.Tasks);
    }

    private static TileResult FollowUps(HomeSnapshot s)
    {
        var week = s.Date.AddDays(7);
        var due = s.Tasks
            .Where(t => t.IsOpen && t.FollowUpOf is not null && t.Due is { } d && d <= week)
            .OrderBy(t => t.Due).ThenBy(t => t.DueTime)
            .ToList();

        var caption = due.Count == 0 ? "None due" : $"Next: {Short(due[0].Title)}, {Day(due[0].Due!.Value, s.Date)}";
        return new TileResult(Count(due.Count), caption, due.Any(t => t.Due < s.Date) ? TileTone.Warning : TileTone.Neutral, TileTarget.Tasks);
    }

    private static TileResult NextReminder(HomeSnapshot s)
    {
        var next = s.Tasks
            .Where(t => t.IsOpen && t.Remind is { } at && at >= s.Now)
            .OrderBy(t => t.Remind)
            .FirstOrDefault();

        if (next is null) return new TileResult("—", "No reminders set", Target: TileTarget.Tasks);

        var at = next.Remind!.Value.LocalDateTime;
        var day = DateOnly.FromDateTime(at);
        var value = day == s.Date ? Clock(TimeOnly.FromDateTime(at)) : $"{Day(day, s.Date)} {Clock(TimeOnly.FromDateTime(at))}";
        return new TileResult(value, Short(next.Title), TileTone.Neutral, TileTarget.Task, next.Id);
    }

    private static TileResult DoneThisWeek(HomeSnapshot s)
    {
        var done = s.Tasks
            .Where(t => t.Closed is { } c && DateOnly.FromDateTime(c.LocalDateTime) >= s.WeekStart)
            .ToList();
        var followedUp = done.Count(t => s.Tasks.Any(f => f.FollowUpOf == t.Id));

        var caption = done.Count == 0 ? "Since Monday" : followedUp == 0 ? "Since Monday" : $"{followedUp} followed up";
        return new TileResult(Count(done.Count), caption, done.Count > 0 ? TileTone.Good : TileTone.Neutral, TileTarget.Tasks);
    }

    private static TileResult DictatedThisWeek(HomeSnapshot s)
    {
        var words = s.Usage.WordsByDay.Where(d => d.Key >= s.WeekStart).Sum(d => d.Value);
        return new TileResult(Compact(words), $"{Compact(s.Usage.TotalWords)} words all time", Target: TileTarget.Insights);
    }

    private static TileResult Overdue(HomeSnapshot s)
    {
        var late = s.Tasks.Where(t => t.IsOpen && t.Due < s.Date && TaskPlan.BucketOf(t, s.Date) == TaskBucket.Overdue)
            .OrderBy(t => t.Due).ToList();
        var caption = late.Count == 0 ? "Nothing late" : $"Oldest: {Day(late[0].Due!.Value, s.Date)}";
        return new TileResult(Count(late.Count), caption, late.Count > 0 ? TileTone.Bad : TileTone.Good, TileTarget.Tasks);
    }

    private static TileResult TimeSaved(HomeSnapshot s)
    {
        var minutes = Math.Max(0, s.Usage.MinutesSavedVsTyping);
        var value = minutes >= 60 ? $"{minutes / 60:0.#} h" : $"{minutes:0} min";
        return new TileResult(value, "Against typing at 40 wpm", Target: TileTarget.Insights);
    }

    private static TileResult Streak(HomeSnapshot s) =>
        new(s.Usage.CurrentStreak == 1 ? "1 day" : $"{s.Usage.CurrentStreak} days",
            $"Longest: {s.Usage.LongestStreak} days", s.Usage.CurrentStreak > 0 ? TileTone.Good : TileTone.Neutral, TileTarget.Insights);

    private static TileResult MeetingsThisWeek(HomeSnapshot s)
    {
        var week = s.Meetings.Count(m => DateOnly.FromDateTime(m.LocalDateTime) >= s.WeekStart);
        var caption = s.Meetings.Count == 0 ? "None recorded yet" : $"Last: {Day(DateOnly.FromDateTime(s.Meetings[0].LocalDateTime), s.Date)}";
        return new TileResult(Count(week), caption, Target: TileTarget.Meetings);
    }

    private static TileResult NextMeeting(HomeSnapshot s)
    {
        if (s.Today is null) return new TileResult("—", "No calendar connected");
        var next = s.Today.Where(e => !e.IsAllDay && e.End > s.Now).OrderBy(e => e.Start).FirstOrDefault();
        if (next is null) return new TileResult("—", "Nothing else today", TileTone.Good);

        var now = next.IsHappeningAt(s.Now);
        return new TileResult(now ? "Now" : Clock(TimeOnly.FromDateTime(next.Start.LocalDateTime)), Short(next.Subject),
            now ? TileTone.Warning : TileTone.Neutral);
    }

    private static TileResult Unread(HomeSnapshot s)
    {
        if (s.Mail is null) return new TileResult("—", "No mailbox connected");
        var unread = s.Mail.Messages.Where(m => m.IsUnread).OrderByDescending(m => m.Received).ToList();
        return new TileResult(Count(unread.Count), unread.Count == 0 ? "All read" : $"Newest: {Short(unread[0].FromName, 24)}");
    }

    // ---- formatting ----

    private static string Count(int n) => n.ToString(CultureInfo.InvariantCulture);

    internal static string Compact(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:0.#}M",
        >= 10_000 => $"{n / 1_000.0:0.#}K",
        _ => n.ToString("N0", Display),
    };

    private static string Short(string text, int max = 34) =>
        text.Length <= max ? text : text[..(max - 1)].TrimEnd() + "…";

    /// <summary>"today", "tomorrow", "yesterday", "Fri", or "Fri 3 Oct" further out.</summary>
    internal static string Day(DateOnly day, DateOnly today) => (day.DayNumber - today.DayNumber) switch
    {
        0 => "today",
        1 => "tomorrow",
        -1 => "yesterday",
        > 1 and < 7 => day.ToString("ddd", Display),
        _ => day.ToString("ddd d MMM", Display),
    };

    /// <summary>"2pm", "9:30am".</summary>
    internal static string Clock(TimeOnly time) =>
        time.ToString(time.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture)
            .ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);
}
