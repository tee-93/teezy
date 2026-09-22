using System.Globalization;
using System.Text.RegularExpressions;

namespace Teezy.Core.Tasks;

/// <summary>What a quick-add line said: the title, and any date, time and category in it.</summary>
public sealed record ParsedTask(string Title, DateOnly? Due, TimeOnly? DueTime, string? Category);

/// <summary>
/// Reads a task typed the way people jot them: "Follow up Cessnock quote fri 2pm #Quotes".
/// </summary>
/// <remarks>
/// <para>
/// Only the last few words are looked at for a date, and only words that are unambiguously dates
/// — "today", "tomorrow", a weekday, "next week", "in 3 days", "25/9" — so a title like "Call
/// Friday Electrical" keeps its words unless they sit at the end. A category is a word after
/// <c>#</c>; underscores stand for spaces.
/// </para>
/// <para>
/// The same reader backs the date boxes in the task panel, so "fri" means the same everywhere.
/// </para>
/// </remarks>
public static partial class TaskInput
{
    [GeneratedRegex(@"(?:^|\s)#(?<cat>[\p{L}\p{N}_\-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CategoryTag();

    [GeneratedRegex(@"^(?<h>\d{1,2})(?::(?<m>\d{2}))?\s?(?<ampm>am|pm)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Clock();

    private static readonly string[] Weekdays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    public static ParsedTask Parse(string text, DateOnly today)
    {
        var line = text.Trim();

        string? category = null;
        if (CategoryTag().Match(line) is { Success: true } tag)
        {
            category = tag.Groups["cat"].Value.Replace('_', ' ');
            line = line.Remove(tag.Index, tag.Length).Trim();
        }

        var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        DateOnly? due = null;
        TimeOnly? time = null;

        // From the end: a time, then a date, each at most once.
        for (var pass = 0; pass < 3 && words.Count > 1; pass++)
        {
            if (time is null && TryTime(words[^1], out var t)) { time = t; words.RemoveAt(words.Count - 1); continue; }

            if (due is null)
            {
                if (words.Count >= 3 && string.Equals(words[^3], "in", StringComparison.OrdinalIgnoreCase)
                    && TryDate($"in {words[^2]} {words[^1]}", today, out var inDays))
                {
                    due = inDays;
                    words.RemoveRange(words.Count - 3, 3);
                    continue;
                }

                if (words.Count >= 2 && TryDate($"{words[^2]} {words[^1]}", today, out var two))
                {
                    due = two;
                    words.RemoveRange(words.Count - 2, 2);
                    continue;
                }

                if (TryDate(words[^1], today, out var one))
                {
                    due = one;
                    words.RemoveAt(words.Count - 1);
                    continue;
                }
            }

            break;
        }

        // A trailing "by" or "on" belonged to the date.
        if (due is not null && words.Count > 1 && words[^1] is "by" or "on" or "By" or "On") words.RemoveAt(words.Count - 1);

        return new ParsedTask(string.Join(' ', words), due, time, category);
    }

    /// <summary>A date typed in a box or at the end of a task: words, weekdays, or day/month.</summary>
    public static bool TryDate(string text, DateOnly today, out DateOnly date)
    {
        var word = text.Trim().ToLowerInvariant();
        date = default;

        switch (word)
        {
            case "today" or "tod":
                date = today;
                return true;
            case "tomorrow" or "tmrw" or "tom":
                date = today.AddDays(1);
                return true;
            case "next week":
                date = today.AddDays(7);
                return true;
        }

        if (word.StartsWith("in ", StringComparison.Ordinal))
        {
            var parts = word.Split(' ');
            if (parts.Length == 3 && int.TryParse(parts[1], out var n) && n is > 0 and < 400)
            {
                if (parts[2].StartsWith("day", StringComparison.Ordinal)) { date = today.AddDays(n); return true; }
                if (parts[2].StartsWith("week", StringComparison.Ordinal)) { date = today.AddDays(7 * n); return true; }
                if (parts[2].StartsWith("month", StringComparison.Ordinal)) { date = today.AddMonths(n); return true; }
            }
            return false;
        }

        // A weekday is the next one after today: "fri" on a Friday means next Friday, since a
        // task due today would say "today".
        var next = word.StartsWith("next ", StringComparison.Ordinal);
        var dayWord = next ? word[5..] : word;
        for (var d = 0; d < 7; d++)
        {
            if (dayWord.Length >= 3 && Weekdays[d].StartsWith(dayWord, StringComparison.Ordinal))
            {
                var ahead = ((d - (int)today.DayOfWeek) + 7) % 7;
                if (ahead == 0) ahead = 7;
                date = today.AddDays(ahead + (next && ahead < 7 ? 7 : 0));
                return true;
            }
        }

        // Day first, the Australian way: 25/9, 25 Sep, 25/9/26, 25/09/2026, or 2026-09-25. With no
        // year it means the next one: "1/2" in September is next February.
        string[] noYear = ["d/M", "d-M", "d MMM", "d MMMM"];
        string[] withYear = ["d/M/yy", "d/M/yyyy", "yyyy-MM-dd", "d-M-yyyy", "d MMM yyyy", "d MMMM yyyy"];

        if (DateTime.TryParseExact(word, noYear, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            date = new DateOnly(today.Year, parsed.Month, Math.Min(parsed.Day, DateTime.DaysInMonth(today.Year, parsed.Month)));
            if (date < today) date = date.AddYears(1);
            return true;
        }

        if (DateTime.TryParseExact(word, withYear, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            date = DateOnly.FromDateTime(parsed);
            return true;
        }

        return false;
    }

    /// <summary>"2pm", "2:30pm", "14:30".</summary>
    public static bool TryTime(string text, out TimeOnly time)
    {
        var word = text.Trim().ToLowerInvariant();
        if (Clock().Match(word) is { Success: true } m)
        {
            var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            var min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
            if (h is >= 1 and <= 12 && min < 60)
            {
                if (m.Groups["ampm"].Value == "pm" && h != 12) h += 12;
                if (m.Groups["ampm"].Value == "am" && h == 12) h = 0;
                time = new TimeOnly(h, min);
                return true;
            }
        }

        if (TimeOnly.TryParseExact(word, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time)
            && word.Contains(':'))
        {
            return true;
        }

        time = default;
        return false;
    }
}
