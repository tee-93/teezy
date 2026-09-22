using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Teezy.Core.Commands;

namespace Teezy.Core.Tasks;

/// <summary>Which task question was asked.</summary>
public enum TaskAsk
{
    None,

    /// <summary>What is due today, late included — the default for "what tasks do I have".</summary>
    Today,

    Tomorrow,

    /// <summary>What is due from today to Sunday.</summary>
    Week,

    Overdue,

    /// <summary>Follow-ups due in the next seven days, late ones included.</summary>
    FollowUps,

    /// <summary>About tasks, but not one of the everyday questions — "when is the Cessnock quote due".</summary>
    Other,
}

/// <summary>What the assistant heard about tasks: a question, a task to add, or one to close.</summary>
/// <remarks>
/// <para>
/// Like <see cref="Calendar.CalendarQuestion"/>: plain patterns, local, instant and free, tested
/// against the phrasings people actually use. Only what they decline reaches Claude.
/// </para>
/// <para>
/// <b>Adding is generous; closing is careful.</b> A wrongly added task costs a tick to remove, so
/// "add a task", "remind me to", "put … on my list" and "new task" are all taken. Closing the
/// wrong one would lose track of a quote, so it needs an explicit verb — "mark … done", "close
/// the … task", "tick off …" — and exactly one open task that clearly matches.
/// </para>
/// </remarks>
public static partial class TaskVoice
{
    // ---- adding ----

    [GeneratedRegex(@"^(?:please\s+|can you\s+|could you\s+)?(?:add|create|make|new|set up)\s+(?:a\s+|an\s+)?(?:new\s+)?(?:task|to-?do|todo)\b[\s,:]*(?:to\s+|for\s+|called\s+|that says\s+)?(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddTask();

    [GeneratedRegex(@"^(?:please\s+|can you\s+|could you\s+)?remind me(?:\s+to|\s+about)?\s+(?<rest>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemindMe();

    [GeneratedRegex(@"^(?:please\s+|can you\s+|could you\s+)?(?:put|add|stick)\s+(?<rest>.+?)\s+(?:on|to|in)\s+(?:my\s+|the\s+)?(?:task\s+list|to-?do\s+list|todo\s+list|list|tasks)(?<tail>.*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PutOnList();

    [GeneratedRegex(@"^(?<when>.{1,32}?)\s+to\s+(?<what>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WhenFirst();

    // ---- closing ----

    [GeneratedRegex(@"^(?:please\s+)?(?:mark|set)\s+(?:the\s+)?(?<what>.+?)(?:\s+task)?\s+(?:as\s+)?(?:done|complete|completed|finished|closed)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkDone();

    [GeneratedRegex(@"^(?:please\s+)?(?:close|complete|finish|tick off|check off|tick)\s+(?:off\s+)?(?:the\s+|my\s+)?(?<what>.+?)(?:\s+task)?(?:\s+off)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CloseTask();

    // ---- spoken times ----

    [GeneratedRegex(@"\b(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?\s*(?<ap>[ap])\.?\s*m\b\.?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ClockWithMeridiem();

    [GeneratedRegex(@"\b(?<w>one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve)(?<rest>\s*(?:[ap]\.?\s*m\b\.?|o'?\s?clock\b))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WordHour();

    [GeneratedRegex(@"\b(?<h>\d{1,2})\s*o'?\s?clock\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OClock();

    [GeneratedRegex(@"\bnoon\b|\bmidday\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Noon();

    private static readonly string[] HourWords =
        ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve"];

    /// <summary>A task to add, if that is what was said: "add a task to chase the Cessnock quote Friday at 2 p.m.".</summary>
    /// <returns>The task, with its day and time read, or null if this was not an add.</returns>
    public static ParsedTask? Add(string? heard, DateOnly today)
    {
        var said = Clean(heard);
        if (said.Length == 0) return null;

        string? rest = null;
        if (AddTask().Match(said) is { Success: true } add) rest = add.Groups["rest"].Value;
        else if (RemindMe().Match(said) is { Success: true } remind) rest = remind.Groups["rest"].Value;
        else if (PutOnList().Match(said) is { Success: true } put) rest = put.Groups["rest"].Value + put.Groups["tail"].Value;

        if (rest is null) return null;
        rest = SpokenTimes(rest);

        // "remind me at 3pm to call Sam", "remind me tomorrow to…": the when came first. Moved
        // to the end, where the reader looks for it, if it really is a when.
        if (WhenFirst().Match(rest) is { Success: true } first)
        {
            var when = TaskInput.Parse("x " + first.Groups["when"].Value, today);
            if (when.Title == "x" && (when.Due is not null || when.DueTime is not null))
            {
                rest = $"{first.Groups["what"].Value} {first.Groups["when"].Value}";
            }
        }

        var parsed = TaskInput.Parse(rest, today);
        if (parsed.Title.Length < 2) return null;

        var title = char.ToUpper(parsed.Title[0], CultureInfo.CurrentCulture) + parsed.Title[1..];
        return parsed with { Title = title.TrimEnd('.', ',', '!', '?') };
    }

    /// <summary>What to close, if that is what was said: the words naming the task.</summary>
    public static string? Close(string? heard)
    {
        var said = Clean(heard);
        if (said.Length == 0) return null;

        var match = MarkDone().Match(said);
        if (!match.Success) match = CloseTask().Match(said);
        return match.Success && match.Groups["what"].Value.Trim().Length > 1 ? match.Groups["what"].Value.Trim() : null;
    }

    /// <summary>
    /// The open task a spoken description names, if exactly one clearly does.
    /// </summary>
    /// <returns>The task, or null with <paramref name="candidates"/> saying why: none, or several.</returns>
    public static TaskItem? Find(string described, IEnumerable<TaskItem> tasks, out IReadOnlyList<TaskItem> candidates)
    {
        var wanted = Words(described);
        candidates = [];
        if (wanted.Count == 0) return null;

        var scored = tasks
            .Where(t => t.IsOpen)
            .Select(t => (Task: t, Score: Score(wanted, Words(t.Title))))
            .Where(x => x.Score >= 0.6)
            .OrderByDescending(x => x.Score)
            .ToList();

        candidates = [.. scored.Select(x => x.Task)];
        if (scored.Count == 0) return null;
        if (scored.Count > 1 && scored[1].Score >= scored[0].Score - 0.001) return null;
        return scored[0].Task;
    }

    /// <summary>How much of what was said appears in the title: 1 when every word does.</summary>
    private static double Score(IReadOnlyList<string> wanted, IReadOnlyList<string> title) =>
        wanted.Count(w => title.Any(t => t.StartsWith(w, StringComparison.Ordinal) || w.StartsWith(t, StringComparison.Ordinal) && t.Length >= 4))
        / (double)wanted.Count;

    private static readonly HashSet<string> Filler =
        ["the", "a", "an", "to", "for", "my", "task", "about", "and", "of", "on", "with", "up", "follow", "that", "one"];

    private static List<string> Words(string text) =>
        [.. CommandMatcher.Normalise(text).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !Filler.Contains(w))];

    /// <summary>"2 p.m." → "2pm", "two o'clock" → "2pm", "at noon" → "12pm", so the reader finds them.</summary>
    internal static string SpokenTimes(string text)
    {
        var result = Noon().Replace(text, "12pm");
        result = WordHour().Replace(result, m =>
            Array.IndexOf(HourWords, m.Groups["w"].Value.ToLowerInvariant()).ToString(CultureInfo.InvariantCulture) + m.Groups["rest"].Value);

        // An hour with no am or pm is read as a working hour: seven to eleven in the morning,
        // twelve to six in the afternoon.
        result = OClock().Replace(result, m =>
        {
            var hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
            return hour is >= 7 and <= 11 ? $"{hour}am" : $"{hour}pm";
        });

        return ClockWithMeridiem().Replace(result, m =>
            m.Groups["h"].Value + (m.Groups["m"].Success ? ":" + m.Groups["m"].Value : string.Empty)
            + m.Groups["ap"].Value.ToLowerInvariant() + "m");
    }

    private static string Clean(string? heard) =>
        (heard ?? string.Empty).Trim().TrimEnd('.', '!', '?', ',').Replace(", ", " ", StringComparison.Ordinal).Trim();

    // ---- questions ----

    [GeneratedRegex(@"\b(tasks?|to ?dos?|todo list|my list|follow ?ups?|followups?|overdue|reminders?|due|what do i need to do|what have i got to do|what s on my plate|what am i doing)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TaskWords();

    [GeneratedRegex(@"^(what|which|any|anything|how many|do i|have i|is there|are there|tell me|show me|list|when|read|give me|whats)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Asking();

    /// <summary>Which task question, if any. <see cref="TaskAsk.None"/> for anything that is not about tasks.</summary>
    public static TaskAsk Classify(string? heard)
    {
        var said = CommandMatcher.Normalise(heard);
        if (said.Length == 0 || !TaskWords().IsMatch(said)) return TaskAsk.None;

        // A question, not dictation that happens to mention a task.
        if (!Asking().IsMatch(said) && !said.Contains("what do i need to do", StringComparison.Ordinal)) return TaskAsk.None;

        if (said.StartsWith("when", StringComparison.Ordinal) || said.StartsWith("which", StringComparison.Ordinal)
            || said.StartsWith("how many", StringComparison.Ordinal) && said.Contains(" about", StringComparison.Ordinal))
        {
            return TaskAsk.Other;
        }

        if (Contains(said, "overdue", "late", "behind")) return TaskAsk.Overdue;
        if (Contains(said, "follow up", "follow ups", "followup", "followups")) return TaskAsk.FollowUps;
        if (Contains(said, "tomorrow")) return TaskAsk.Tomorrow;
        if (Contains(said, "this week", "the week", "week")) return TaskAsk.Week;
        return TaskAsk.Today;
    }

    private static bool Contains(string said, params string[] words) =>
        words.Any(w => Regex.IsMatch(said, $@"\b{w}\b"));
}

/// <summary>Spoken answers about tasks, composed here from the list — no network, no model.</summary>
public static class TaskAnswer
{
    /// <summary>Four is about as much as anyone takes in by ear; the rest are counted.</summary>
    public const int NamedAtMost = 4;

    private static readonly CultureInfo Display = CultureInfo.GetCultureInfo("en-AU");

    public static string? For(TaskAsk ask, IReadOnlyList<TaskItem> tasks, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var open = tasks.Where(t => t.IsOpen).ToList();

        return ask switch
        {
            TaskAsk.Today => Today(open, today),
            TaskAsk.Tomorrow => OnDay(open, today.AddDays(1), "tomorrow", today),
            TaskAsk.Week => Week(open, today),
            TaskAsk.Overdue => Overdue(open, today),
            TaskAsk.FollowUps => FollowUps(open, today),
            _ => null,
        };
    }

    /// <summary>A clause to add to a diary answer: "You also have three tasks due, one late."</summary>
    public static string? Also(IReadOnlyList<TaskItem> tasks, DateOnly day, DateOnly today)
    {
        var due = day == today
            ? TaskPlan.DueToday(tasks, today)
            : [.. tasks.Where(t => t.IsOpen && t.Due == day)];
        if (due.Count == 0) return null;

        var late = due.Count(t => t.Due < today);
        var count = due.Count == 1 ? "one task" : $"{Number(due.Count)} tasks";
        return late == 0 ? $"You also have {count} due." : $"You also have {count} due, {Number(late)} of them late.";
    }

    private static string Today(List<TaskItem> open, DateOnly today)
    {
        var due = TaskPlan.DueToday(open, today);
        if (due.Count == 0)
        {
            var tomorrow = open.Count(t => t.Due == today.AddDays(1));
            return tomorrow == 0
                ? "Nothing due today."
                : $"Nothing due today. Tomorrow has {Count(tomorrow)}.";
        }

        var late = due.Count(t => t.Due < today);
        var head = late == 0 ? $"{Capital(Count(due.Count))} today" : $"{Capital(Count(due.Count))} today, {Number(late)} late";
        return $"{head}: {Listed(due, t => Name(t, today, withDay: false))}.";
    }

    private static string OnDay(List<TaskItem> open, DateOnly day, string when, DateOnly today)
    {
        var due = open.Where(t => t.Due == day).OrderBy(t => t.DueTime ?? TimeOnly.MaxValue).ToList();
        return due.Count == 0
            ? $"Nothing due {when}."
            : $"{Capital(Count(due.Count))} {when}: {Listed(due, t => Name(t, today, withDay: false))}.";
    }

    private static string Week(List<TaskItem> open, DateOnly today)
    {
        var sunday = today.AddDays((7 - (int)today.DayOfWeek) % 7);
        var due = open.Where(t => t.Due is { } d && d >= today && d <= sunday)
            .OrderBy(t => t.Due).ThenBy(t => t.DueTime ?? TimeOnly.MaxValue).ToList();
        var late = open.Count(t => t.Due < today);
        var tail = late == 0 ? string.Empty : $" And {Number(late)} already late.";

        return due.Count == 0
            ? $"Nothing else due this week.{tail}"
            : $"{Capital(Count(due.Count))} this week: {Listed(due, t => Name(t, today, withDay: true))}.{tail}";
    }

    private static string Overdue(List<TaskItem> open, DateOnly today)
    {
        var late = open.Where(t => t.Due < today && TaskPlan.BucketOf(t, today) == TaskBucket.Overdue).OrderBy(t => t.Due).ToList();
        return late.Count == 0
            ? "Nothing's late."
            : $"{Capital(Count(late.Count))} late: {Listed(late, t => $"{t.Title}, due {Day(t.Due!.Value, today)}")}.";
    }

    private static string FollowUps(List<TaskItem> open, DateOnly today)
    {
        var due = open.Where(t => t.FollowUpOf is not null && t.Due is { } d && d <= today.AddDays(7))
            .OrderBy(t => t.Due).ToList();
        if (due.Count == 0) return "No follow-ups due this week.";

        var count = due.Count == 1 ? "One follow-up" : $"{Capital(Number(due.Count))} follow-ups";
        return $"{count} this week: {Listed(due, t => $"{Bare(t.Title)} {Day(t.Due!.Value, today)}")}.";
    }

    /// <summary>A task as said aloud: its title, and its time or day.</summary>
    private static string Name(TaskItem task, DateOnly today, bool withDay)
    {
        var name = task.Title;
        if (withDay && task.Due is { } due && due != today) name += $" {Day(due, today)}";
        if (task.DueTime is { } time) name += $" at {Clock(time)}";
        return name;
    }

    /// <summary>"Follow up: Send quote" reads as "Send quote" in a list that already says follow-ups.</summary>
    private static string Bare(string title) =>
        title.StartsWith("Follow up: ", StringComparison.OrdinalIgnoreCase) ? title[11..] : title;

    private static string Listed(IReadOnlyList<TaskItem> tasks, Func<TaskItem, string> name)
    {
        var named = tasks.Take(NamedAtMost).Select(name).ToList();
        var more = tasks.Count - named.Count;
        var list = named.Count switch
        {
            1 => named[0],
            _ => string.Join(", ", named.Take(named.Count - 1)) + " and " + named[^1],
        };
        return more > 0 ? $"{list}, and {Number(more)} more" : list;
    }

    /// <summary>"today", "tomorrow", "yesterday", "on Friday", "on 3 October".</summary>
    private static string Day(DateOnly day, DateOnly today) => (day.DayNumber - today.DayNumber) switch
    {
        0 => "today",
        1 => "tomorrow",
        -1 => "yesterday",
        > 1 and < 7 => $"on {day.ToString("dddd", Display)}",
        < -1 and > -7 => $"last {day.ToString("dddd", Display)}",
        _ => $"on {day.ToString("d MMMM", Display)}",
    };

    private static string Clock(TimeOnly time) =>
        time.ToString(time.Minute == 0 ? "h tt" : "h:mm tt", CultureInfo.InvariantCulture)
            .ToLowerInvariant().Replace(" ", string.Empty, StringComparison.Ordinal);

    private static string Count(int n) => n == 1 ? "one task" : $"{Number(n)} tasks";

    private static string Number(int n) => n switch
    {
        1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five",
        6 => "six", 7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>The open tasks, as material for an unusual question. Titles, dates and categories only — never emails.</summary>
    /// <remarks>
    /// Task titles and notes are the user's own words, but an attached email is someone else's,
    /// so emails are left out entirely; notes are left out too, since they can quote them.
    /// </remarks>
    public static UntrustedMaterial Material(IReadOnlyList<TaskItem> tasks, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var text = new StringBuilder();
        foreach (var task in tasks.Where(t => t.IsOpen).OrderBy(t => t.Due ?? DateOnly.MaxValue).Take(40))
        {
            text.Append("- ").Append(task.Title);
            if (task.Due is { } due)
            {
                text.Append(" | due ").Append(due.ToString("dddd d MMMM", Display));
                if (task.DueTime is { } time) text.Append(' ').Append(Clock(time));
                if (due < today) text.Append(" (late)");
            }
            if (task.Category is { } category) text.Append(" | ").Append(category);
            if (task.FollowUpOf is not null) text.Append(" | follow-up");
            text.AppendLine();
        }

        return new UntrustedMaterial(MaterialKind.Tasks, text.Length == 0 ? "(no open tasks)" : text.ToString());
    }
}
