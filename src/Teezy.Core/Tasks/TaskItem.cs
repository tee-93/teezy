namespace Teezy.Core.Tasks;

/// <summary>A note on a task: when, who, and what.</summary>
/// <param name="By">Who wrote it: the name set in Settings ▸ Tasks, or "TeezyFlow" for notes it
/// writes itself (closed, followed up). Null on notes from before 1.16.</param>
public sealed record TaskNote(DateTimeOffset At, string Text, string? By = null)
{
    /// <summary>The author of notes TeezyFlow writes itself.</summary>
    public const string App = "TeezyFlow";

    /// <summary>
    /// Written by TeezyFlow rather than a person. Notes from before 1.16 carry no author, so
    /// TeezyFlow's own are known by their wording.
    /// </summary>
    public bool IsFromApp => By == App
        || (By is null && (Text.StartsWith("Closed, and followed up", StringComparison.Ordinal)
                           || Text.StartsWith("Email attached:", StringComparison.Ordinal)
                           || Text.StartsWith("Created from an email", StringComparison.Ordinal)));
}

/// <summary>An email attached to a task — dropped or pasted in — kept apart from the notes.</summary>
/// <param name="Added">When it was attached.</param>
/// <param name="Body">Its text. Someone else's writing: shown, and sent to Claude only on request.</param>
public sealed record TaskEmail(DateTimeOffset Added, string Subject, string? From, DateTimeOffset? Received, string Body);

/// <summary>One task.</summary>
/// <param name="Id">Stable across computers, so sync can match the same task on each.</param>
/// <param name="Title">What to do.</param>
/// <param name="Category">One of the categories in Settings ▸ Tasks, e.g. "Quotes". Null for none.</param>
/// <param name="Start">
/// Retired from the page in 1.16 — a task starts when it is made. Still honoured for tasks that
/// have one, which sit under "Not started" until that day.
/// </param>
/// <param name="Due">The day it is due. Null for no date.</param>
/// <param name="DueTime">A time on the due day. Null for any time that day.</param>
/// <param name="Remind">When to pop the reminder card, independent of the due date. Null for none.</param>
/// <param name="Emails">Emails attached to the task, oldest first.</param>
/// <param name="Advice">The last next steps or draft reply, as edited by the user.</param>
/// <param name="Notes">Running notes, oldest first.</param>
/// <param name="Created">When it was made.</param>
/// <param name="Closed">When it was closed; null while open.</param>
/// <param name="FollowUpOf">The task this follows up, which makes a chain: quote sent, chased, chased again.</param>
/// <param name="Modified">When it last changed, anywhere. The newer copy wins when computers disagree.</param>
/// <param name="Deleted">Deleted, kept as a marker so the deletion reaches the other computers.</param>
/// <param name="Reminded">When its reminder was shown, so it is shown once.</param>
/// <param name="Pinned">On the focus list — the small always-on-top card for calls. Travels with sync.</param>
public sealed record TaskItem(
    string Id,
    string Title,
    string? Category,
    DateOnly? Start,
    DateOnly? Due,
    TimeOnly? DueTime,
    IReadOnlyList<TaskNote> Notes,
    DateTimeOffset Created,
    DateTimeOffset? Closed,
    string? FollowUpOf,
    DateTimeOffset Modified,
    bool Deleted = false,
    DateTimeOffset? Reminded = null,
    DateTimeOffset? Remind = null,
    IReadOnlyList<TaskEmail>? Emails = null,
    string? Advice = null,
    bool Pinned = false)
{
    public bool IsOpen => Closed is null && !Deleted;

    /// <summary>The attached emails, never null.</summary>
    public IReadOnlyList<TaskEmail> AllEmails => Emails ?? [];

    public static TaskItem New(string title, DateTimeOffset now, string? category = null,
        DateOnly? start = null, DateOnly? due = null, TimeOnly? dueTime = null, string? followUpOf = null,
        DateTimeOffset? remind = null) =>
        new(Guid.NewGuid().ToString("N"), title.Trim(), Clean(category), start, due, dueTime, [], now, null, followUpOf, now,
            Remind: remind);

    internal static string? Clean(string? category) =>
        string.IsNullOrWhiteSpace(category) ? null : category.Trim();
}

/// <summary>Where an open task sits on the list, in the order the list shows them.</summary>
public enum TaskBucket
{
    Overdue,
    Today,
    Upcoming,
    NoDate,
    NotStarted,
}

/// <summary>How the list is arranged, and what "today" means for it.</summary>
public static class TaskPlan
{
    /// <summary>Where an open task belongs on a given day.</summary>
    /// <remarks>
    /// A start date in the future parks a task under Not started whatever its due date, so a
    /// follow-up booked for next Friday does not clutter today. Once started it is placed by its
    /// due date; a started task with no due date is No date.
    /// </remarks>
    public static TaskBucket BucketOf(TaskItem task, DateOnly today) =>
        task.Start is { } start && start > today ? TaskBucket.NotStarted
        : task.Due is not { } due ? TaskBucket.NoDate
        : due < today ? TaskBucket.Overdue
        : due == today ? TaskBucket.Today
        : TaskBucket.Upcoming;

    /// <summary>
    /// The focus list: pinned open tasks, late and dated ones first by when they are due, then
    /// undated ones in the order they were pinned (oldest first, so a call list reads top down).
    /// </summary>
    public static IReadOnlyList<TaskItem> Focus(IEnumerable<TaskItem> tasks) =>
        tasks.Where(t => t.IsOpen && t.Pinned)
            .OrderBy(t => t.Due is null)
            .ThenBy(t => t.Due)
            .ThenBy(t => t.DueTime ?? TimeOnly.MaxValue)
            .ThenBy(t => t.Created)
            .ToList();

    /// <summary>Open tasks grouped by bucket, each group due-first then newest.</summary>
    public static IReadOnlyList<(TaskBucket Bucket, IReadOnlyList<TaskItem> Tasks)> Arrange(
        IEnumerable<TaskItem> tasks, DateOnly today, string? category = null) =>
        tasks
            .Where(t => t.IsOpen)
            .Where(t => category is null || string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase))
            .GroupBy(t => BucketOf(t, today))
            .OrderBy(g => g.Key)
            .Select(g => (g.Key, (IReadOnlyList<TaskItem>)g
                .OrderBy(t => t.Due ?? DateOnly.MaxValue)
                .ThenBy(t => t.DueTime ?? TimeOnly.MaxValue)
                .ThenByDescending(t => t.Created)
                .ToList()))
            .ToList();

    /// <summary>What needs doing today: overdue, then due today.</summary>
    public static IReadOnlyList<TaskItem> DueToday(IEnumerable<TaskItem> tasks, DateOnly today) =>
        Arrange(tasks, today)
            .Where(g => g.Bucket is TaskBucket.Overdue or TaskBucket.Today)
            .SelectMany(g => g.Tasks)
            .ToList();

    /// <summary>Open tasks whose reminder time has come and whose reminder has not been shown here.</summary>
    /// <remarks>
    /// Only tasks with a reminder set: a task due on a day is for the day's list, not a pop-up.
    /// A reminder missed while the computer was off is shown when it next looks.
    /// </remarks>
    public static IReadOnlyList<TaskItem> DueForReminder(IEnumerable<TaskItem> tasks, DateTimeOffset now) =>
        tasks
            .Where(t => t.IsOpen && t.Reminded is null && t.Remind is { } at && at <= now)
            .OrderBy(t => t.Remind)
            .ToList();

    /// <summary>A day and a time on this computer's clock, as a moment.</summary>
    public static DateTimeOffset At(DateOnly day, TimeOnly time) =>
        new(day.ToDateTime(time), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(time)));

    /// <summary>The day itself, or the Monday after if it falls on a weekend — for follow-ups at work.</summary>
    public static DateOnly Workday(DateOnly day) => day.DayOfWeek switch
    {
        DayOfWeek.Saturday => day.AddDays(2),
        DayOfWeek.Sunday => day.AddDays(1),
        _ => day,
    };

    /// <summary>Every category in use, for the pickers, in name order.</summary>
    public static IReadOnlyList<string> Categories(IEnumerable<TaskItem> tasks) =>
        tasks.Where(t => !t.Deleted && t.Category is not null)
            .Select(t => t.Category!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The whole follow-up chain a task belongs to, oldest first.</summary>
    public static IReadOnlyList<TaskItem> Chain(IReadOnlyList<TaskItem> tasks, string id)
    {
        var byId = tasks.Where(t => !t.Deleted).ToDictionary(t => t.Id);
        if (!byId.TryGetValue(id, out var task)) return [];

        // Up to the first task, guarding against a loop.
        var first = task;
        var seen = new HashSet<string> { first.Id };
        while (first.FollowUpOf is { } parent && byId.TryGetValue(parent, out var up) && seen.Add(up.Id)) first = up;

        // Then down, following each task's follow-up.
        var chain = new List<TaskItem> { first };
        var children = byId.Values.Where(t => t.FollowUpOf is not null).ToLookup(t => t.FollowUpOf!);
        var current = first;
        while (children[current.Id].OrderBy(t => t.Created).FirstOrDefault() is { } next && !chain.Contains(next))
        {
            chain.Add(next);
            current = next;
        }

        return chain;
    }
}
