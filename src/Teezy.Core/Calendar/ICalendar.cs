namespace Teezy.Core.Calendar;

/// <summary>A connected calendar account, read only.</summary>
/// <remarks>
/// <para>
/// <b>Read only, and enforced at the provider rather than here.</b> Both accounts are
/// authorised with read-only scopes — <c>Calendars.Read</c> and
/// <c>calendar.readonly</c> — so a bug in Teezy cannot move a meeting, and neither can anything
/// that talks Teezy into trying. A permission that was never granted is a far better guarantee
/// than a code path that promises not to.
/// </para>
/// <para>
/// <b>Events are untrusted text.</b> Subjects and locations are written by whoever sent the
/// invitation, including people outside the organisation, and they end up in a prompt. Until
/// now the assistant was blind — it saw only the user's own words, which removed prompt
/// injection entirely. This is the feature that ends that, so the rule replacing it has to be
/// structural rather than a promise:
/// </para>
/// <para>
/// <b>An LLM request that contains calendar content is given no tools.</b> It can produce
/// words and nothing else. Something hidden in a meeting subject can therefore make the answer
/// wrong, or rude, but it cannot open an application, change the volume, or reach any other
/// capability — because none were offered in that request. Actions continue to come only from
/// the user's own voice, through the closed command list.
/// </para>
/// </remarks>
public interface ICalendar
{
    /// <summary>Which account this reads.</summary>
    CalendarSource Source { get; }

    /// <summary>Whether it is connected and able to answer.</summary>
    bool IsConnected { get; }

    /// <summary>Events starting within the window, soonest first.</summary>
    /// <remarks>
    /// A window rather than a count: "what is on today" and "what is next" want different
    /// spans of the same data, and asking for a fixed number of events answers neither well.
    /// </remarks>
    /// <exception cref="CalendarUnavailableException">The account could not be reached.</exception>
    Task<IReadOnlyList<CalendarEvent>> BetweenAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}

/// <summary>A connected calendar that could not be read.</summary>
/// <remarks>
/// Distinct from an empty diary, and said differently. "Your calendar is empty this afternoon"
/// and "I couldn't reach your calendar" call for opposite responses, and collapsing them would
/// have someone walk confidently into a meeting they were never told about.
/// </remarks>
public sealed class CalendarUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
