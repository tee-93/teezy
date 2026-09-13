using System.Net;
using Ical.Net.DataTypes;
using Teezy.Core.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;
using IcsFeed = Ical.Net.Calendar;

namespace Teezy.Connectors;

/// <summary>A calendar read from a published ICS link.</summary>
/// <remarks>
/// <para>
/// <b>The way in when signing in is not allowed.</b> A work tenant commonly blocks third-party
/// apps from asking for calendar access, and an app registration there needs an administrator.
/// Outlook can instead publish a calendar as a link the user copies; reading that link needs
/// nobody's consent but theirs.
/// </para>
/// <para>
/// <b>Read-only by nature.</b> A published feed cannot be written to, so this holds the same
/// promise as the signed-in calendars with nothing to enforce it but the format itself.
/// </para>
/// <para>
/// <b>The link is a secret.</b> Anyone who has it can read the calendar, so it is kept in the
/// secret store with the tokens, never in settings, and only ever fetched over HTTPS.
/// </para>
/// <para>
/// <b>Cached for ten minutes.</b> A published Outlook feed can carry years of history, and
/// Outlook only refreshes what it publishes every so often, so fetching it for every question
/// would cost time and data for an answer no fresher than the cached one.
/// </para>
/// </remarks>
public sealed class IcsCalendar(
    Func<string?> link,
    HttpClient? http = null,
    Func<DateTimeOffset>? now = null) : ICalendar
{
    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly TimeSpan FreshFor = TimeSpan.FromMinutes(10);

    /// <summary>How far before a window occurrences are asked for.</summary>
    /// <remarks>
    /// Ical.Net lists occurrences by when they start, so a fortnight's leave that began last week
    /// would be missed by a question about today without it. Five weeks covers any leave or trip
    /// worth showing; overlap with the window is still checked for every one.
    /// </remarks>
    private static readonly TimeSpan LookBack = TimeSpan.FromDays(35);

    private readonly HttpClient _http = http ?? Shared;
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IcsFeed? _feed;
    private string? _feedFrom;
    private DateTimeOffset _fetched;

    public CalendarSource Source => CalendarSource.Ics;

    public bool IsConnected => Normalise(link()) is not null;

    public async Task<IReadOnlyList<CalendarEvent>> BetweenAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var feed = await FeedAsync(ct).ConfigureAwait(false);
        return Between(feed, from, to);
    }

    /// <summary>Checks a link returns a readable calendar, for Settings to call before saving it.</summary>
    /// <exception cref="CalendarUnavailableException">It does not, and why.</exception>
    public static async Task CheckAsync(string link, HttpClient? http = null, CancellationToken ct = default) =>
        _ = Parse(await DownloadAsync(
            Normalise(link) ?? throw new CalendarUnavailableException(
                "That isn’t a calendar link. It should start with https:// or webcal:// and end in .ics."),
            http ?? Shared,
            ct).ConfigureAwait(false));

    private async Task<IcsFeed> FeedAsync(CancellationToken ct)
    {
        var url = Normalise(link())
                  ?? throw new CalendarUnavailableException("No calendar link is saved.", needsReconnect: true);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_feed is not null && _feedFrom == url && _now() - _fetched < FreshFor) return _feed;

            _feed = Parse(await DownloadAsync(url, _http, ct).ConfigureAwait(false));
            _feedFrom = url;
            _fetched = _now();
            return _feed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<string> DownloadAsync(string url, HttpClient http, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct).ConfigureAwait(false);

            // Gone for good rather than down for now: an unpublished calendar answers like this,
            // and waiting will not bring it back.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new CalendarUnavailableException(
                    "The calendar link no longer works. It may have been unpublished or replaced with a new one.",
                    needsReconnect: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new CalendarUnavailableException(
                    $"The calendar link answered with an error ({(int)response.StatusCode}).");
            }

            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException
                                      || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new CalendarUnavailableException("Couldn’t reach your calendar link.", e);
        }
    }

    /// <summary>The feed, or why it could not be read.</summary>
    internal static IcsFeed Parse(string body)
    {
        // The HTML link Outlook offers beside the ICS one is the likeliest thing to be pasted by
        // mistake, and it deserves a message that says so rather than a parse error.
        if (!body.TrimStart().StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase))
        {
            throw new CalendarUnavailableException(
                "That link didn’t return a calendar. Check it’s the ICS link, not the HTML one.");
        }

        try
        {
            return IcsFeed.Load(body)
                   ?? throw new CalendarUnavailableException("The calendar link returned an empty calendar.");
        }
        catch (Exception e) when (e is not CalendarUnavailableException)
        {
            throw new CalendarUnavailableException("The calendar link returned something unreadable.", e);
        }
    }

    /// <summary>Every occurrence that overlaps the window, repeats expanded, soonest first.</summary>
    internal static IReadOnlyList<CalendarEvent> Between(IcsFeed feed, DateTimeOffset from, DateTimeOffset to)
    {
        var start = new CalDateTime(from.UtcDateTime - LookBack, "UTC");
        List<CalendarEvent> events = [];

        foreach (var occurrence in feed.GetOccurrences(start)
                     .TakeWhile(o => o.Period.StartTime.AsUtc < to.UtcDateTime))
        {
            if (occurrence.Source is not IcalEvent source || IsCancelled(source)) continue;

            var read = Read(source, occurrence.Period);
            var overlaps = read.End > read.Start
                ? read.Start < to && read.End > from
                : read.Start >= from && read.Start < to;

            if (overlaps) events.Add(read);
        }

        events.Sort((a, b) => a.Start.CompareTo(b.Start));
        return events;
    }

    /// <summary>
    /// Cancelled meetings, by status or by the "Canceled:" prefix Outlook writes into the title of
    /// a meeting cancelled by its organiser but still sitting in the attendee's calendar.
    /// </summary>
    private static bool IsCancelled(IcalEvent source) =>
        string.Equals(source.Status, "CANCELLED", StringComparison.OrdinalIgnoreCase)
        || source.Summary?.StartsWith("Canceled:", StringComparison.OrdinalIgnoreCase) == true
        || source.Summary?.StartsWith("Cancelled:", StringComparison.OrdinalIgnoreCase) == true;

    private static CalendarEvent Read(IcalEvent source, Period period)
    {
        var begins = period.StartTime;
        var ends = period.EffectiveEndTime;
        var allDay = !begins.HasTime;

        DateTimeOffset start, end;
        if (allDay)
        {
            // A date, not an instant — anchored to local midnight like the other connectors, so
            // leave on the fifteenth cannot drift onto the fourteenth.
            start = CalendarWeek.Midnight(begins.Date);
            end = ends is { } finish ? CalendarWeek.Midnight(finish.Date) : start.AddDays(1);
            if (end <= start) end = start.AddDays(1);
        }
        else
        {
            start = Instant(begins);
            end = ends is { } finish ? Instant(finish) : start;
        }

        return new CalendarEvent(
            source.Summary is { Length: > 0 } subject ? subject.Trim() : "(no subject)",
            start,
            end,
            allDay,
            source.Location is { Length: > 0 } place ? place.Trim() : null,
            CalendarSource.Ics);
    }

    /// <summary>A timed slot as local time. A time with no zone at all is taken as local.</summary>
    private static DateTimeOffset Instant(CalDateTime time)
    {
        if (time.TzId is null)
        {
            var local = DateTime.SpecifyKind(time.Value, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }

        return new DateTimeOffset(DateTime.SpecifyKind(time.AsUtc, DateTimeKind.Utc)).ToLocalTime();
    }

    /// <summary>The link as an HTTPS address, or null if it is not one.</summary>
    /// <remarks>
    /// webcal:// is how calendar links are often shared and is just HTTPS under another name.
    /// Plain http is refused: the link is the only thing guarding the calendar, and sending it
    /// unencrypted would hand it to anyone on the same network.
    /// </remarks>
    internal static string? Normalise(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return null;

        var candidate = link.Trim();
        if (candidate.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase))
        {
            candidate = "https://" + candidate["webcal://".Length..];
        }

        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? uri.AbsoluteUri
            : null;
    }
}
