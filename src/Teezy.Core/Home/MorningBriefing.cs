using System.Globalization;
using System.Text;
using Teezy.Core.Tasks;

namespace Teezy.Core.Home;

/// <summary>One line in the briefing: what, a detail beside it, and the task it opens, if any.</summary>
public sealed record BriefingItem(string Text, string? Detail, string? TaskId = null, bool Late = false, bool Meeting = false);

/// <summary>A group in the briefing: "Late", "Today", "Follow-ups this week".</summary>
public sealed record BriefingSection(string Title, IReadOnlyList<BriefingItem> Items, bool Warning = false);

/// <summary>The morning briefing, ready to show or read aloud.</summary>
/// <param name="Headline">The day in one line — the same <see cref="DayBrief"/> line Home opens with.</param>
/// <param name="Yesterday">"Yesterday you closed three tasks.", or null for none.</param>
public sealed record Briefing(string Greeting, string Date, string Headline, IReadOnlyList<BriefingSection> Sections, string? Yesterday)
{
    public bool IsEmpty => Sections.Count == 0;
}

/// <summary>Builds the weekday morning briefing, and decides when it is due.</summary>
/// <remarks>
/// <para>
/// <b>A plain list, made here.</b> Late tasks, then the day — timed tasks, reminders and meetings
/// in order, then what is due at any time — then the week's follow-ups, and a word about
/// yesterday. Nothing leaves the computer unless the AI summary is switched on, and then only
/// through a request that carries no tools (<see cref="IBriefingWriter"/>).
/// </para>
/// <para>
/// <b>Once a day, per computer, at the first look after the chosen time.</b> A laptop that was
/// off at 8:30 shows it when it is next opened that morning — or that afternoon — rather than
/// never.
/// </para>
/// </remarks>
public static class MorningBriefing
{
    private static readonly CultureInfo Display = CultureInfo.GetCultureInfo("en-AU");

    public static readonly TimeOnly DefaultTime = new(8, 30);

    /// <summary>Whether to show it now: a chosen day, past the chosen time, not yet shown today.</summary>
    public static bool IsDue(DateTimeOffset now, TimeOnly at, bool weekends, DateOnly? shownOn)
    {
        var local = now.LocalDateTime;
        var today = DateOnly.FromDateTime(local);
        var weekend = local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        return (weekends || !weekend) && TimeOnly.FromDateTime(local) >= at && shownOn != today;
    }

    public static Briefing For(HomeSnapshot s, string name)
    {
        var today = s.Date;
        var open = s.Tasks.Where(t => t.IsOpen).ToList();
        List<BriefingSection> sections = [];

        var late = open.Where(t => TaskPlan.BucketOf(t, today) == TaskBucket.Overdue).OrderBy(t => t.Due).ToList();
        if (late.Count > 0)
        {
            sections.Add(new BriefingSection("Late", [.. late.Select(t => new BriefingItem(
                t.Title, $"due {Day(t.Due!.Value, today)}", t.Id, Late: true))], Warning: true));
        }

        // The day, in order: meetings and anything with a time, then what can be done whenever.
        var timed = new List<(DateTime At, BriefingItem Item)>();
        var dueToday = open.Where(t => TaskPlan.BucketOf(t, today) == TaskBucket.Today).ToList();
        foreach (var task in dueToday.Concat(open.Where(t => !dueToday.Contains(t) && !late.Contains(t) && RemindsToday(t, today))).Distinct())
        {
            if (TimeToday(task, today) is { } at)
            {
                timed.Add((at, new BriefingItem(task.Title, Clock(TimeOnly.FromDateTime(at)), task.Id)));
            }
        }

        if (s.Today is { } events)
        {
            foreach (var e in events.Where(e => e.IsAllDay || e.End > s.Now))
            {
                timed.Add((e.IsAllDay ? today.ToDateTime(TimeOnly.MinValue) : e.Start.LocalDateTime,
                    new BriefingItem(e.Subject, e.IsAllDay ? "all day" : Clock(TimeOnly.FromDateTime(e.Start.LocalDateTime)), Meeting: true)));
            }
        }

        var anyTime = dueToday.Where(t => TimeToday(t, today) is null).ToList();
        List<BriefingItem> day = [.. timed.OrderBy(x => x.At).Select(x => x.Item), .. anyTime.Select(t => new BriefingItem(t.Title, "any time", t.Id))];
        if (day.Count > 0) sections.Add(new BriefingSection("Today", day));

        var followUps = open
            .Where(t => t.FollowUpOf is not null && t.Due is { } d && d > today && d <= today.AddDays(7))
            .OrderBy(t => t.Due).ToList();
        if (followUps.Count > 0)
        {
            sections.Add(new BriefingSection("Follow-ups this week", [.. followUps.Select(t => new BriefingItem(
                t.Title.StartsWith("Follow up: ", StringComparison.OrdinalIgnoreCase) ? t.Title[11..] : t.Title,
                Day(t.Due!.Value, today), t.Id))]));
        }

        if (s.Mail is { } mail && mail.Messages.Count(m => m.IsUnread) is var unread and > 0)
        {
            sections.Add(new BriefingSection("Inbox", [new BriefingItem(unread == 1 ? "1 unread email" : $"{unread} unread emails", null)]));
        }

        // Yesterday: the last working day, so Monday's briefing speaks of Friday.
        var previous = today.AddDays(today.DayOfWeek == DayOfWeek.Monday ? -3 : -1);
        var closed = s.Tasks.Count(t => t.Closed is { } c && DateOnly.FromDateTime(c.LocalDateTime) == previous);
        var yesterday = closed == 0 ? null
            : $"{(previous == today.AddDays(-1) ? "Yesterday" : $"On {previous.ToString("dddd", Display)}")} you closed {(closed == 1 ? "one task" : $"{closed} tasks")}.";

        return new Briefing(
            $"{DayBrief.Greeting(s.Now)}, {name}",
            s.Now.LocalDateTime.ToString("dddd d MMMM", Display),
            DayBrief.For(s),
            sections,
            yesterday);
    }

    /// <summary>The briefing as one short passage, for reading aloud.</summary>
    public static string Spoken(Briefing b)
    {
        var text = new StringBuilder($"{b.Greeting}. {b.Headline}");
        foreach (var section in b.Sections.Where(s => s.Title != "Inbox"))
        {
            var named = section.Items.Take(4).Select(i => i.Detail is { Length: > 0 } d && section.Title != "Late" ? $"{i.Text}, {d}" : i.Text).ToList();
            var more = section.Items.Count - named.Count;
            text.Append($" {section.Title}: {string.Join("; ", named)}{(more > 0 ? $"; and {more} more" : string.Empty)}.");
        }

        if (b.Yesterday is { } y) text.Append(' ').Append(y);
        return text.ToString();
    }

    /// <summary>
    /// What the optional AI summary is written from: the user's own tasks and latest notes, and
    /// today's meeting subjects. Never an attached email.
    /// </summary>
    /// <remarks>
    /// Meeting subjects are written by whoever sent the invitation, so the writer that reads this
    /// must carry no tools — see <see cref="IBriefingWriter"/>.
    /// </remarks>
    public static string Material(HomeSnapshot s)
    {
        var today = s.Date;
        var text = new StringBuilder();

        text.AppendLine("Open tasks (the user's own words):");
        foreach (var task in s.Tasks.Where(t => t.IsOpen).OrderBy(t => t.Due ?? DateOnly.MaxValue).Take(30))
        {
            text.Append("- ").Append(task.Title);
            if (task.Due is { } due)
            {
                text.Append(" | due ").Append(due.ToString("dddd d MMMM", Display));
                if (task.DueTime is { } time) text.Append(' ').Append(Clock(time));
                if (due < today) text.Append(" (late)");
            }
            if (task.Category is { } category) text.Append(" | ").Append(category);
            if (task.FollowUpOf is not null) text.Append(" | follow-up");
            if (task.Notes.LastOrDefault(n => !n.IsFromApp) is { } note)
            {
                var line = note.Text.Replace('\n', ' ');
                text.Append(" | latest note: ").Append(line.Length > 160 ? line[..160] + "…" : line);
            }
            text.AppendLine();
        }

        if (s.Today is { Count: > 0 } events)
        {
            text.AppendLine();
            text.AppendLine("Today's meetings (subjects written by whoever sent the invitation):");
            foreach (var e in events.OrderBy(e => e.Start))
            {
                text.Append("- ").Append(e.IsAllDay ? "all day" : Clock(TimeOnly.FromDateTime(e.Start.LocalDateTime))).Append(' ').AppendLine(e.Subject);
            }
        }

        return text.ToString();
    }

    private static bool RemindsToday(TaskItem task, DateOnly today) =>
        task.Remind is { } at && DateOnly.FromDateTime(at.LocalDateTime) == today;

    private static DateTime? TimeToday(TaskItem task, DateOnly today)
    {
        if (task.Due == today && task.DueTime is { } time) return today.ToDateTime(time);
        if (RemindsToday(task, today)) return task.Remind!.Value.LocalDateTime;
        return null;
    }

    private static string Day(DateOnly day, DateOnly today) => (day.DayNumber - today.DayNumber) switch
    {
        0 => "today",
        1 => "tomorrow",
        -1 => "yesterday",
        > 1 and < 7 => day.ToString("dddd", Display),
        < -1 and > -7 => $"last {day.ToString("dddd", Display)}",
        _ => day.ToString("ddd d MMM", Display),
    };

    private static string Clock(TimeOnly time) => HomeTiles.Clock(time);
}

/// <summary>Writes the optional AI summary at the top of the briefing.</summary>
/// <remarks>
/// <b>An implementation must send no tools</b>: the material includes meeting subjects, which
/// strangers write — the same rule as <see cref="IUntrustedNarrator"/>. It returns text to show,
/// and nothing else.
/// </remarks>
public interface IBriefingWriter
{
    bool IsAvailable { get; }

    /// <summary>Two or three sentences on how to tackle the day, or null if nothing useful came back.</summary>
    /// <exception cref="Commands.AssistantUnavailableException">It could not be reached.</exception>
    Task<string?> SummariseAsync(string material, DateTimeOffset now, CancellationToken ct = default);
}
