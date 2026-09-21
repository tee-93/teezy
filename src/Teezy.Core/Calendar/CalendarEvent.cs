namespace Teezy.Core.Calendar;

/// <summary>Which account an event came from.</summary>
public enum CalendarSource
{
    Microsoft,
    Google,

    /// <summary>A calendar published as an ICS link, read without signing in to anything.</summary>
    Ics,

    /// <summary>
    /// Events read from a file on this computer — written there by a Power Automate flow, for a
    /// work calendar that can neither be signed in to nor published.
    /// </summary>
    File,
}

/// <summary>One thing in the diary, reduced to what an answer needs.</summary>
/// <param name="Subject">What it is called.</param>
/// <param name="Start">Local start time.</param>
/// <param name="End">Local end time.</param>
/// <param name="IsAllDay">All-day events answer "what's on today" but never "what's next".</param>
/// <param name="Location">Where, if the organiser said. Often a meeting link, often nothing.</param>
/// <param name="Source">Which account, so two calendars can be told apart in one answer.</param>
/// <remarks>
/// <para>
/// Deliberately thin. Attendees, bodies, attachments, organiser addresses and conference
/// details are all available from both providers and none of them are fetched, because every
/// field that exists here is a field that could end up in a prompt.
/// </para>
/// <para>
/// <b>Nothing here is trusted text.</b> A subject or a location is written by whoever sent the
/// invitation, which may be someone outside the organisation, and it reaches an LLM. See
/// <see cref="ICalendar"/> for the rule that makes that safe.
/// </para>
/// </remarks>
public sealed record CalendarEvent(
    string Subject,
    DateTimeOffset Start,
    DateTimeOffset End,
    bool IsAllDay,
    string? Location,
    CalendarSource Source)
{
    /// <summary>How long it runs for.</summary>
    public TimeSpan Duration => End - Start;

    /// <summary>True once it has started and not yet finished.</summary>
    public bool IsHappeningAt(DateTimeOffset when) => Start <= when && End > when;
}
