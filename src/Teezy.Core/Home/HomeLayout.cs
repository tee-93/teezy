namespace Teezy.Core.Home;

/// <summary>A tile or panel Home can show: its key, its name in Customise, and what it needs.</summary>
/// <param name="NeedsCalendar">Offered only once a calendar is connected.</param>
/// <param name="NeedsMail">Offered only once a mailbox is connected.</param>
public sealed record HomePart(string Key, string Label, string Description, bool NeedsCalendar = false, bool NeedsMail = false)
{
    public bool IsAvailable(bool calendar, bool mail) => (!NeedsCalendar || calendar) && (!NeedsMail || mail);
}

/// <summary>What Home can show, what it shows by default, and keeping a saved choice sensible.</summary>
/// <remarks>
/// <para>
/// Modelled on Pursiva's widget bar: a saved choice is only an ordered list of keys, and every
/// read passes through <see cref="Sanitise"/>, so an old or hand-edited list — or one synced
/// from a computer with a newer version — can never break the page.
/// </para>
/// <para>
/// <b>Accounts are extras.</b> The work laptop cannot connect Microsoft or Google, so every
/// default works with no account at all; calendar and mail parts join only where connected. A
/// part that needs a missing account is hidden rather than shown empty, but kept in the saved
/// list (<see cref="Merge"/>), so the personal laptop's choice survives a sync through the work one.
/// </para>
/// </remarks>
public static class HomeLayout
{
    public const int MaxTiles = 5;

    public static readonly IReadOnlyList<HomePart> Tiles =
    [
        new("due_today", "Due today", "Tasks due today, and how many are late"),
        new("follow_ups", "Follow-ups this week", "Follow-ups due in the next seven days"),
        new("next_reminder", "Next reminder", "The next reminder you have set"),
        new("done_week", "Done this week", "Tasks closed since Monday"),
        new("dictated_week", "Dictated this week", "Words dictated since Monday"),
        new("overdue", "Overdue", "Tasks past their due date"),
        new("time_saved", "Time saved", "Time saved dictating instead of typing"),
        new("streak", "Streak", "Days in a row you have dictated"),
        new("meetings_week", "Meetings recorded", "Meetings recorded this week"),
        new("quotes_open", "Quotes out", "What your open quotes are worth, and what needs chasing"),
        new("won_month", "Won this month", "Quotes won since the first of the month"),
        new("next_meeting", "Next meeting", "Your next meeting today", NeedsCalendar: true),
        new("unread", "Unread", "Unread email", NeedsMail: true),
    ];

    public static readonly IReadOnlyList<HomePart> LeftPanels =
    [
        new("today", "Today", "The day on one timeline: tasks, reminders and meetings"),
        new("week", "This week", "Monday to Sunday: what is due each day, and meetings"),
    ];

    public static readonly IReadOnlyList<HomePart> RightPanels =
    [
        new("coming_up", "Coming up", "Tasks due in the next seven days"),
        new("quotes", "Quotes to chase", "Quotes due a chase, and the ones that have gone quiet"),
        new("notes", "Recent notes", "The latest notes across your tasks"),
        new("meetings", "Meetings", "Meetings you have recorded, and their notes"),
        new("inbox", "Inbox", "Unread email", NeedsMail: true),
    ];

    public static readonly IReadOnlyList<string> DefaultTiles = ["due_today", "quotes_open", "follow_ups", "next_reminder", "won_month"];

    public static readonly IReadOnlyList<string> DefaultLeft = ["today", "week"];

    public static readonly IReadOnlyList<string> DefaultRight = ["quotes", "coming_up", "notes", "meetings", "inbox"];

    /// <summary>
    /// What to show: known keys only, each once, only those available here, capped — or the
    /// defaults when nothing usable is left.
    /// </summary>
    public static IReadOnlyList<string> Sanitise(
        IEnumerable<string>? saved, IReadOnlyList<HomePart> catalogue, IReadOnlyList<string> defaults,
        bool calendar, bool mail, int max = int.MaxValue)
    {
        List<string> Usable(IEnumerable<string> keys) =>
            [.. keys
                .Where(k => catalogue.FirstOrDefault(p => p.Key == k) is { } part && part.IsAvailable(calendar, mail))
                .Distinct(StringComparer.Ordinal)
                .Take(max)];

        var chosen = Usable(saved ?? []);
        return chosen.Count > 0 ? chosen : Usable(defaults);
    }

    /// <summary>
    /// The list to save after editing what is shown here: the edited keys, then any saved key
    /// hidden here for want of an account, so it is still there on the computer that has one.
    /// </summary>
    public static IReadOnlyList<string> Merge(
        IReadOnlyList<string> edited, IEnumerable<string>? saved, IReadOnlyList<HomePart> catalogue, bool calendar, bool mail)
    {
        var hidden = (saved ?? [])
            .Where(k => catalogue.FirstOrDefault(p => p.Key == k) is { } part && !part.IsAvailable(calendar, mail))
            .Where(k => !edited.Contains(k));
        return [.. edited.Concat(hidden).Distinct(StringComparer.Ordinal)];
    }

    public static HomePart? Find(string key) =>
        Tiles.Concat(LeftPanels).Concat(RightPanels).FirstOrDefault(p => p.Key == key);
}
