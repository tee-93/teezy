using System.Globalization;
using System.Text.RegularExpressions;

namespace Teezy.Core.Calendar;

/// <summary>
/// Reads a meeting out of the label New Outlook gives it for screen readers.
/// </summary>
/// <remarks>
/// <para>
/// New Outlook's calendar names every meeting for accessibility tools in one line:
/// <c>Weekly sync, 10:00 AM to 10:30 AM, Monday, September 22, 2026, Level 3, Busy, Recurring event</c>,
/// or for a whole day <c>Leave, all day event, Monday, September 14, 2026 to Friday, September 18, 2026, Free</c>.
/// Reading those is how TeezyFlow sees a work calendar that allows no other way in — no API, no
/// sign-in, no flow: only what Outlook already shows on this computer.
/// </para>
/// <para>
/// <b>Anchored on the time, not split on commas,</b> because a subject may contain commas and
/// the time is the one part with a fixed shape. Everything before it is the subject. Dates are
/// accepted both US-style (<c>September 22, 2026</c>) and the Australian way (<c>22 September
/// 2026</c>), and times with or without AM/PM, since the work laptop's region decides.
/// </para>
/// </remarks>
public static partial class OutlookLabel
{
    private const string Time = @"\d{1,2}[:.]\d{2}(?:\s?[AaPp]\.?\s?[Mm]\.?)?";
    private const string Day = @"(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday)";
    private const string Date = Day + @",\s(?:[A-Za-z]+\s\d{1,2},\s\d{4}|\d{1,2}\s[A-Za-z]+,?\s\d{4})";

    [GeneratedRegex(@"^(?<subject>.*?),\s(?<start>" + Time + @")\sto\s(?<end>" + Time + @"),\s(?<date>" + Date + @")(?:\sto\s(?<date2>" + Date + @"))?(?:,\s(?<rest>.*))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex Timed();

    [GeneratedRegex(@"^(?<subject>.*?),\sall\sday\sevent,\s(?<date>" + Date + @")(?:\sto\s(?<date2>" + Date + @"))?(?:,\s(?<rest>.*))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex AllDay();

    /// <summary>The words Outlook appends after the location, which are not part of it.</summary>
    private static readonly HashSet<string> Trailers = new(StringComparer.OrdinalIgnoreCase)
    {
        "Free", "Busy", "Tentative", "Away", "Out of office", "Working elsewhere",
        "Recurring event", "Exception to a recurring event", "Private", "Has attachments",
        "Online meeting", "Microsoft Teams Meeting", "Draft", "Not responded", "Accepted", "Declined",
    };

    private static readonly CultureInfo[] Cultures =
        [CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("en-AU"), CultureInfo.GetCultureInfo("en-GB")];

    /// <summary>The meeting a label describes, in this computer's time zone, or null if it is not one.</summary>
    public static CalendarEvent? Parse(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;

        if (AllDay().Match(label) is { Success: true } whole)
        {
            if (!TryDate(whole.Groups["date"].Value, out var first)) return null;
            var last = whole.Groups["date2"].Success && TryDate(whole.Groups["date2"].Value, out var d2) ? d2 : first;
            if (Cancelled(whole.Groups["subject"].Value)) return null;

            return new CalendarEvent(
                whole.Groups["subject"].Value.Trim(),
                Local(first, TimeOnly.MinValue),
                Local(last.AddDays(1), TimeOnly.MinValue),
                true,
                Location(whole.Groups["rest"].Value),
                CalendarSource.File);
        }

        if (Timed().Match(label) is { Success: true } timed)
        {
            if (!TryDate(timed.Groups["date"].Value, out var day)) return null;
            if (!TryTime(timed.Groups["start"].Value, out var from) || !TryTime(timed.Groups["end"].Value, out var to)) return null;
            if (Cancelled(timed.Groups["subject"].Value)) return null;

            // A meeting that runs past midnight names its last day; without one, an end earlier
            // than the start still means the next morning.
            var endDay = timed.Groups["date2"].Success && TryDate(timed.Groups["date2"].Value, out var d2) ? d2
                : to < from ? day.AddDays(1) : day;

            return new CalendarEvent(
                timed.Groups["subject"].Value.Trim(),
                Local(day, from),
                Local(endDay, to),
                false,
                Location(timed.Groups["rest"].Value),
                CalendarSource.File);
        }

        return null;
    }

    private static bool Cancelled(string subject) =>
        subject.StartsWith("Canceled:", StringComparison.OrdinalIgnoreCase)
        || subject.StartsWith("Cancelled:", StringComparison.OrdinalIgnoreCase);

    /// <summary>What is left after the status words, which is the location if there is one.</summary>
    private static string? Location(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest)) return null;

        var parts = rest.Split(", ").ToList();
        while (parts.Count > 0 && (Trailers.Contains(parts[^1].Trim()) || parts[^1].StartsWith("By ", StringComparison.Ordinal)))
        {
            parts.RemoveAt(parts.Count - 1);
        }

        var where = string.Join(", ", parts).Trim();
        return where.Length > 0 ? where : null;
    }

    private static bool TryDate(string text, out DateOnly date)
    {
        // The weekday is only a reading aid; the date after it is the date.
        var comma = text.IndexOf(", ", StringComparison.Ordinal);
        var bare = comma >= 0 ? text[(comma + 2)..] : text;

        foreach (var culture in Cultures)
        {
            if (DateTime.TryParse(bare, culture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                date = DateOnly.FromDateTime(parsed);
                return true;
            }
        }

        date = default;
        return false;
    }

    private static bool TryTime(string text, out TimeOnly time)
    {
        var tidy = text.Replace('.', ':').Replace("a:m:", "AM", StringComparison.OrdinalIgnoreCase)
            .Replace("p:m:", "PM", StringComparison.OrdinalIgnoreCase).Replace("a:m", "AM", StringComparison.OrdinalIgnoreCase)
            .Replace("p:m", "PM", StringComparison.OrdinalIgnoreCase).Trim();

        foreach (var culture in Cultures)
        {
            if (DateTime.TryParse($"2000-01-01 {tidy}", culture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                time = TimeOnly.FromDateTime(parsed);
                return true;
            }
        }

        time = default;
        return false;
    }

    private static DateTimeOffset Local(DateOnly day, TimeOnly time)
    {
        var local = day.ToDateTime(time, DateTimeKind.Local);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
    }
}
