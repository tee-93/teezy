using System.Globalization;
using System.Text.Json;
using Teezy.Core.Calendar;

namespace Teezy.Connectors;

/// <summary>
/// A calendar read from a file on this computer, written by a Power Automate flow.
/// </summary>
/// <remarks>
/// <para>
/// <b>The route for a work calendar that allows nothing else.</b> The New Outlook has no local
/// interface to read; the organisation will not consent to TeezyFlow reading its calendars; and
/// publishing a calendar is switched off. What such an organisation usually does allow is Power
/// Automate, Microsoft's own tool, inside the person's own account. A flow there reads the
/// calendar every quarter of an hour and writes it to a file in the work OneDrive, which syncs to
/// the work laptop — so TeezyFlow reads a file, and the calendar never leaves the company.
/// </para>
/// <para>
/// The file is exactly what the flow's "Get calendar view of events (V3)" action returns: an
/// object with a <c>value</c> array. A bare array is accepted too. The README spells out the
/// flow, step by step.
/// </para>
/// <para>
/// Read-only by construction: this class opens a file and parses it, and there is no path from
/// here back to the calendar.
/// </para>
/// </remarks>
public sealed class FileCalendar(Func<string?> path) : ICalendar
{
    /// <summary>Older than this and the flow has probably stopped; the answer says so.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    public CalendarSource Source => CalendarSource.File;

    public bool IsConnected => path() is { Length: > 0 } p && File.Exists(p);

    public async Task<IReadOnlyList<CalendarEvent>> BetweenAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (path() is not { Length: > 0 } file || !File.Exists(file))
        {
            throw new CalendarUnavailableException(
                "The calendar file isn’t there. Check OneDrive is syncing on this computer.",
                needsReconnect: true);
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            throw new CalendarUnavailableException("The calendar file couldn’t be read just now.", e);
        }

        return Parse(json)
            .Where(e => e.End > from && e.Start < to)
            .OrderBy(e => e.Start)
            .ToList();
    }

    /// <summary>Checks a file is one TeezyFlow can read, for Settings to call before saving it.</summary>
    /// <exception cref="CalendarUnavailableException">It is not, and why.</exception>
    public static void Check(string file)
    {
        if (!File.Exists(file)) throw new CalendarUnavailableException("That file doesn’t exist.");
        _ = Parse(File.ReadAllText(file));
    }

    /// <summary>
    /// Reads the events out of a Power Automate calendar view.
    /// </summary>
    /// <remarks>
    /// Times prefer the <c>...WithTimeZone</c> fields, which carry their offset. The plain
    /// <c>start</c>/<c>end</c> fields are UTC unless the flow asked otherwise, and are read as UTC.
    /// All-day events are the exception: their date is the date, not an instant, so it is kept
    /// as a local day rather than shifted across midnight by a time zone.
    /// </remarks>
    /// <exception cref="CalendarUnavailableException">The file is not a calendar view.</exception>
    public static IReadOnlyList<CalendarEvent> Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new CalendarUnavailableException("That file isn’t a calendar from Power Automate.", e);
        }

        using (doc)
        {
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var v) ? v
                : throw new CalendarUnavailableException(
                    "That file isn’t a calendar from Power Automate. It should hold the output of “Get calendar view of events (V3)”.");

            var events = new List<CalendarEvent>();
            foreach (var item in items.EnumerateArray())
            {
                var subject = Text(item, "subject") ?? "(no subject)";

                // Outlook keeps a cancelled meeting in the diary, renamed, until it is removed.
                if (subject.StartsWith("Canceled:", StringComparison.OrdinalIgnoreCase)
                    || subject.StartsWith("Cancelled:", StringComparison.OrdinalIgnoreCase)
                    || Bool(item, "isCancelled"))
                {
                    continue;
                }

                var allDay = Bool(item, "isAllDay");
                if (Instant(item, "start", allDay) is not { } start) continue;
                var end = Instant(item, "end", allDay) ?? start.AddHours(1);

                events.Add(new CalendarEvent(
                    subject,
                    start,
                    end,
                    allDay,
                    Text(item, "location") is { Length: > 0 } where ? where : null,
                    CalendarSource.File));
            }

            return events;
        }
    }

    private static DateTimeOffset? Instant(JsonElement item, string name, bool allDay)
    {
        if (allDay)
        {
            // The day, in this computer's time zone.
            var raw = Text(item, name) ?? Text(item, name + "WithTimeZone");
            if (raw is null || raw.Length < 10
                || !DateOnly.TryParseExact(raw[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return null;
            }

            var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local);
            return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }

        if (Text(item, name + "WithTimeZone") is { } withZone
            && DateTimeOffset.TryParse(withZone, CultureInfo.InvariantCulture, DateTimeStyles.None, out var zoned))
        {
            return zoned.ToLocalTime();
        }

        if (Text(item, name) is { } plain
            && DateTimeOffset.TryParse(plain, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
        {
            return utc.ToLocalTime();
        }

        return null;
    }

    private static string? Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Bool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
