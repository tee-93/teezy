namespace Teezy.Core.Tasks;

/// <summary>A flagged email, as the task list shows it.</summary>
/// <param name="Id">Outlook's own id for the item, to act on it later.</param>
/// <param name="From">The sender's display name. Empty for a plain Outlook task.</param>
/// <param name="Subject">What it is about.</param>
/// <param name="Received">When it arrived, or when the task was made.</param>
/// <param name="Due">The flag's due date, if it has one.</param>
/// <param name="Categories">Its Outlook categories, in Outlook's order.</param>
/// <param name="IsEmail">An email that was flagged, rather than a task typed into Outlook.</param>
public sealed record MailTask(
    string Id,
    string From,
    string Subject,
    DateTimeOffset Received,
    DateOnly? Due,
    IReadOnlyList<string> Categories,
    bool IsEmail)
{
    /// <summary>The group it sits under: its first category, or none.</summary>
    public string Group => Categories.Count > 0 ? Categories[0] : TaskList.NoCategory;

    /// <summary>Due before today and not done.</summary>
    public bool IsOverdue(DateOnly today) => Due is { } due && due < today;
}

/// <summary>A group of tasks under one category heading.</summary>
public sealed record TaskGroup(string Name, IReadOnlyList<MailTask> Tasks);

/// <summary>How the task list is arranged.</summary>
public static class TaskList
{
    public const string NoCategory = "No category";

    /// <summary>
    /// Tasks grouped by their first category, as Outlook's own "By category" view does.
    /// </summary>
    /// <remarks>
    /// Within a group, due work first — overdue at the top, then by due date — and undated
    /// tasks after, newest first, because the newest flag is usually the live one. Groups in
    /// name order, with uncategorised last, so the categories you chose come before the pile.
    /// </remarks>
    public static IReadOnlyList<TaskGroup> Arrange(IEnumerable<MailTask> tasks) =>
        tasks
            .GroupBy(t => t.Group, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key == NoCategory ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TaskGroup(g.First().Group, g
                .OrderBy(t => t.Due is null ? 1 : 0)
                .ThenBy(t => t.Due)
                .ThenByDescending(t => t.Received)
                .ToList()))
            .ToList();
}

/// <summary>Where the task list comes from, and where changes to it go back to.</summary>
/// <remarks>
/// Implemented over classic Outlook on Windows. Every change is one Outlook would make itself —
/// a flag completed or restored, categories set — so the two stay the same list.
/// </remarks>
public interface IMailTasks
{
    /// <summary>Whether the source can be read right now, e.g. Outlook is running.</summary>
    bool IsAvailable { get; }

    /// <summary>Everything flagged and not yet done.</summary>
    Task<IReadOnlyList<MailTask>> OpenAsync(CancellationToken ct = default);

    /// <summary>Marks it done, as ticking the flag in Outlook does.</summary>
    Task CompleteAsync(string id, CancellationToken ct = default);

    /// <summary>Puts the flag back, for an undo.</summary>
    Task ReopenAsync(string id, CancellationToken ct = default);

    /// <summary>The text of the email, for the AI, and only when asked for.</summary>
    Task<string?> BodyAsync(string id, CancellationToken ct = default);

    /// <summary>Opens it in Outlook's own window.</summary>
    Task OpenInOutlookAsync(string id, CancellationToken ct = default);
}
