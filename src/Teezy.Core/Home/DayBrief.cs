using Teezy.Core.Tasks;

namespace Teezy.Core.Home;

/// <summary>Home's greeting and its one-line daily update, built on this computer from what is there.</summary>
/// <remarks>
/// Unlike <see cref="DaySummary"/>, which speaks only of the diary and inbox and so said nothing
/// on a computer with no accounts, this leads with tasks — always present — and adds meetings
/// and mail when they are connected. It is never blank: a clear day says so.
/// </remarks>
public static class DayBrief
{
    /// <summary>"Good morning", "Good afternoon" or "Good evening", by the clock.</summary>
    public static string Greeting(DateTimeOffset now) => now.LocalDateTime.Hour switch
    {
        < 12 => "Good morning",
        < 17 => "Good afternoon",
        _ => "Good evening",
    };

    /// <summary>The day in one line, clauses joined with " · ".</summary>
    public static string For(HomeSnapshot s)
    {
        List<string> clauses = [];

        var due = TaskPlan.DueToday(s.Tasks, s.Date);
        var late = due.Count(t => t.Due < s.Date);
        if (due.Count > 0)
        {
            var tasks = due.Count == 1 ? "1 task today" : $"{due.Count} tasks today";
            clauses.Add(late == 0 ? tasks : $"{tasks}, {late} late");
        }

        var week = s.Date.AddDays(7);
        var followUps = s.Tasks.Count(t => t.IsOpen && t.FollowUpOf is not null && t.Due is { } d && d >= s.Date && d <= week);
        if (followUps > 0) clauses.Add(followUps == 1 ? "1 follow-up this week" : $"{followUps} follow-ups this week");

        if (s.Today is { } events)
        {
            var left = events.Where(e => !e.IsAllDay && e.End > s.Now).OrderBy(e => e.Start).ToList();
            if (left.Count > 0)
            {
                var first = left[0];
                clauses.Add(first.IsHappeningAt(s.Now)
                    ? $"in {first.Subject} now"
                    : $"{(left.Count == 1 ? "a meeting" : $"{left.Count} meetings")}, the next at {HomeTiles.Clock(TimeOnly.FromDateTime(first.Start.LocalDateTime))}");
            }
        }

        if (s.Mail is { } mail)
        {
            var unread = mail.Messages.Count(m => m.IsUnread);
            if (unread > 0) clauses.Add(unread == 1 ? "1 unread email" : $"{unread} unread emails");
        }

        if (clauses.Count == 0) return "A clear day — nothing due. Add a task below, or drag an email in.";

        var line = string.Join(" · ", clauses);
        return char.ToUpperInvariant(line[0]) + line[1..] + ".";
    }
}
