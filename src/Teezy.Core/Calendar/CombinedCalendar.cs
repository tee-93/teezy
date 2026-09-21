namespace Teezy.Core.Calendar;

/// <summary>What came back when the diary was read, including what did not.</summary>
/// <param name="Events">Everything found, soonest first, across every account that answered.</param>
/// <param name="Unavailable">The accounts that could not be reached.</param>
/// <remarks>
/// The two travel together deliberately. An answer built from half the accounts is not wrong
/// so much as dangerously incomplete, and the difference only exists if the missing half is
/// carried alongside the events rather than discarded on the way.
/// </remarks>
public sealed record CalendarReading(
    IReadOnlyList<CalendarEvent> Events,
    IReadOnlyList<CalendarSource> Unavailable)
{
    public static CalendarReading Empty { get; } = new([], []);

    /// <summary>Nothing answered at all, so there is no diary here — only a failure.</summary>
    public bool NothingAnswered => Events.Count == 0 && Unavailable.Count > 0;
}

/// <summary>Every connected account, read as one diary.</summary>
/// <remarks>
/// <para>
/// A list rather than one Microsoft and one Google, because two accounts from the same provider
/// is the ordinary case for anyone with a job, and a shape allowing only one would have to be
/// unpicked the first time that mattered.
/// </para>
/// <para>
/// <b>One account failing does not fail the answer.</b> It is reported instead, so the reply
/// can say what it could not see. Silently answering "nothing this afternoon" because a token
/// expired is how someone misses a meeting, which is the one outcome this feature exists to
/// prevent.
/// </para>
/// </remarks>
/// <param name="accounts">
/// Asked each time rather than captured once, so connecting or disconnecting an account in
/// Settings takes effect on the next question instead of on the next launch.
/// </param>
public sealed class CombinedCalendar(Func<IReadOnlyList<ICalendar>> accounts)
{
    /// <summary>A fixed set, for tests and for anywhere the list genuinely cannot change.</summary>
    public CombinedCalendar(IReadOnlyList<ICalendar> accounts) : this(() => accounts) { }

    public bool IsConnected => accounts().Any(a => a.IsConnected);

    /// <summary>Reads every connected account at once and merges what comes back.</summary>
    /// <remarks>
    /// Concurrently, because the answer waits for the slowest and two accounts asked in turn
    /// would take twice as long for no reason.
    /// </remarks>
    public async Task<CalendarReading> BetweenAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var connected = accounts().Where(a => a.IsConnected).ToList();
        if (connected.Count == 0) return CalendarReading.Empty;

        var readings = await Task
            .WhenAll(connected.Select(a => ReadOneAsync(a, from, to, ct)))
            .ConfigureAwait(false);

        List<CalendarEvent> events = [];
        List<CalendarSource> unavailable = [];

        foreach (var (found, failed) in readings)
        {
            if (found is not null) events.AddRange(found);
            if (failed is { } source && !unavailable.Contains(source)) unavailable.Add(source);
        }

        // The same diary reached two ways — an account signed in and the same calendar read from
        // Outlook's window, or a link to it — would otherwise list every meeting twice. Same
        // subject at the same times is the same meeting, whichever way it arrived.
        events = [.. events.DistinctBy(e => (e.Subject.Trim(), e.Start.UtcDateTime, e.End.UtcDateTime, e.IsAllDay))];

        events.Sort(Soonest);

        return new CalendarReading(events, unavailable);
    }

    /// <summary>One account's events, or which account failed — never a thrown exception.</summary>
    private static async Task<(IReadOnlyList<CalendarEvent>? Found, CalendarSource? Failed)>
        ReadOneAsync(ICalendar account, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        try
        {
            return (await account.BetweenAsync(from, to, ct).ConfigureAwait(false), null);
        }
        catch (CalendarUnavailableException)
        {
            return (null, account.Source);
        }
    }

    /// <summary>
    /// Soonest first, and all-day events before timed ones on the same day.
    /// </summary>
    /// <remarks>
    /// An all-day event starts at midnight, so ordering on start alone already puts it first —
    /// this only settles the case where a meeting genuinely begins at midnight too, and does so
    /// the way a day is read out rather than arbitrarily.
    /// </remarks>
    private static int Soonest(CalendarEvent a, CalendarEvent b)
    {
        var when = a.Start.CompareTo(b.Start);
        if (when != 0) return when;

        if (a.IsAllDay != b.IsAllDay) return a.IsAllDay ? -1 : 1;

        return string.Compare(a.Subject, b.Subject, StringComparison.CurrentCulture);
    }
}
