using System.Globalization;
using System.Text;

namespace Teezy.Core.Calendar;

/// <summary>Turns a diary into a sentence, without asking anyone.</summary>
/// <remarks>
/// <para>
/// The whole point of <see cref="CalendarQuestion"/>'s two tiers. "What's next" and "what's on
/// today" are the questions actually asked out loud, they have one right answer each, and
/// paying a second and a fraction of a penny to have a model rephrase a list is absurd. This
/// composes the reply directly: instant, offline, free, and identical every time.
/// </para>
/// <para>
/// <b>Written to be read aloud.</b> The reply goes into a small floating pill and, when voice
/// replies are on, through a speech synthesiser — so no lists, no markdown, no "you have the
/// following items", and times spoken the way a person says them.
/// </para>
/// </remarks>
public static class CalendarAnswer
{
    /// <summary>How many events get named before the rest are counted instead.</summary>
    /// <remarks>
    /// Four is about as much as anyone takes in from one spoken sentence. A packed day answered
    /// in full is not an answer, it is a recital.
    /// </remarks>
    private const int NamedAtMost = 4;

    /// <summary>The span of diary a question needs.</summary>
    /// <remarks>
    /// Chosen here rather than at the call site so the window and the wording cannot disagree —
    /// a reply saying "nothing else today" built from a week of events would be wrong in a way
    /// that is very hard to see.
    /// </remarks>
    public static (DateTimeOffset From, DateTimeOffset To) Window(
        CalendarAsk ask, DateTimeOffset now)
    {
        var midnight = Midnight(now);

        return ask switch
        {
            // From now, so "what's on today" at six in the evening is not a recital of the
            // morning. Overlap means anything still running is still included.
            CalendarAsk.Today => (now, midnight),

            CalendarAsk.Tomorrow => (midnight, midnight.AddDays(1)),

            // A week, so "what's next" on a Friday afternoon does not answer "nothing" when
            // Monday morning starts with a meeting.
            _ => (now, now.AddDays(7)),
        };
    }

    /// <summary>The reply, or null if this is a question only the smarter tier can answer.</summary>
    public static string? For(CalendarAsk ask, CalendarReading reading, DateTimeOffset now)
    {
        // Nothing answered at all is a failure wearing the costume of an empty diary, and
        // saying "nothing on" would be the single most harmful thing this feature could do.
        if (reading.NothingAnswered) return Missing(reading.Unavailable);

        var answer = ask switch
        {
            CalendarAsk.Next => Next(reading.Events, now),

            // "Nothing else today" rather than "nothing today", because the morning may well
            // have been full and saying otherwise sounds like a calendar that is not working.
            CalendarAsk.Today => Listed(Today(reading.Events, now), "today", "Nothing else today."),
            CalendarAsk.Tomorrow => Listed(reading.Events, "tomorrow", "Nothing tomorrow."),
            _ => null,
        };

        if (answer is null) return null;

        return reading.Unavailable.Count == 0
            ? answer
            : $"{answer} {Missing(reading.Unavailable)}";
    }

    // ---- the three questions ----

    private static string Next(IReadOnlyList<CalendarEvent> events, DateTimeOffset now)
    {
        // All-day events are excluded on purpose: "you're on leave" is not an answer to "what's
        // next", and it would otherwise claim the slot for the whole day.
        var timed = events.Where(e => !e.IsAllDay).ToList();

        if (timed.FirstOrDefault(e => e.IsHappeningAt(now)) is { } current)
        {
            return $"You’re in {current.Subject} until {Clock(current.End)}.";
        }

        if (timed.FirstOrDefault(e => e.Start > now) is not { } next)
        {
            return "Nothing in your calendar for the next week.";
        }

        var minutes = (int)Math.Round((next.Start - now).TotalMinutes);

        // Under an hour, how long away is more useful than what o'clock it is — it is the thing
        // the question was really asking.
        return minutes <= 60
            ? $"{next.Subject} in {Minutes(minutes)}."
            : $"Next is {next.Subject}{Day(next.Start, now)} at {Clock(next.Start)}.";
    }

    private static string Listed(
        IReadOnlyList<CalendarEvent> events, string when, string nothing)
    {
        if (events.Count == 0) return nothing;

        var named = events.Take(NamedAtMost).ToList();
        var rest = events.Count - named.Count;

        var line = new StringBuilder($"{Count(events.Count)} {when}: ");

        for (var i = 0; i < named.Count; i++)
        {
            if (i > 0) line.Append(i == named.Count - 1 && rest == 0 ? " and " : ", ");
            line.Append(Describe(named[i]));
        }

        if (rest > 0) line.Append($", and {rest} more");

        return line.Append('.').ToString();
    }

    /// <summary>Today's events, minus the ones already over.</summary>
    /// <remarks>
    /// The provider returns anything overlapping the window, which correctly includes the
    /// meeting currently running and today's all-day events — but at four in the afternoon
    /// nobody asking what is on today wants this morning read back.
    /// </remarks>
    private static IReadOnlyList<CalendarEvent> Today(
        IReadOnlyList<CalendarEvent> events, DateTimeOffset now) =>
        [.. events.Where(e => e.IsAllDay || e.End > now)];

    // ---- saying it ----

    private static string Describe(CalendarEvent occurrence) =>
        occurrence.IsAllDay
            ? $"{occurrence.Subject} all day"
            : $"{occurrence.Subject} at {Clock(occurrence.Start)}";

    /// <summary>A time the way it is said rather than the way it is stored.</summary>
    /// <remarks>
    /// Invariant culture on purpose. This is spoken English either way, and a machine set to a
    /// 24-hour locale would otherwise have the pill say "at 14:00", which no one says.
    /// </remarks>
    private static string Clock(DateTimeOffset when)
    {
        var local = when.ToLocalTime();

        // "%h" rather than "h": a single-character format string is read as a standard
        // specifier, and there is no standard "h", so the bare form throws at runtime.
        var face = local.ToString(
            local.Minute == 0 ? "%h" : "h:mm", CultureInfo.InvariantCulture);

        return face + (local.Hour < 12 ? "am" : "pm");
    }

    /// <summary>The end of today, where tomorrow begins.</summary>
    /// <remarks>
    /// Built from the local offset on the day itself rather than from today's, so a window that
    /// straddles a daylight-saving change is still an hour longer or shorter as it should be.
    /// </remarks>
    private static DateTimeOffset Midnight(DateTimeOffset now)
    {
        var tomorrow = now.ToLocalTime().Date.AddDays(1);
        return new DateTimeOffset(tomorrow, TimeZoneInfo.Local.GetUtcOffset(tomorrow));
    }

    /// <summary>Which day, when it is not this one.</summary>
    private static string Day(DateTimeOffset when, DateTimeOffset now)
    {
        var days = (when.ToLocalTime().Date - now.ToLocalTime().Date).Days;

        return days switch
        {
            <= 0 => "",
            1 => " tomorrow",

            // Inside the week a day name places it instantly; beyond that "on Tuesday" is
            // ambiguous about which Tuesday.
            < 7 => $" on {when.ToLocalTime():dddd}",
            _ => $" on {when.ToLocalTime().ToString("d MMMM", CultureInfo.InvariantCulture)}",
        };
    }

    private static string Minutes(int minutes) => minutes switch
    {
        <= 0 => "a moment",
        1 => "a minute",
        60 => "an hour",
        _ => $"{minutes} minutes",
    };

    /// <summary>Small numbers in words, because that is how they are read out.</summary>
    private static string Count(int count) => count switch
    {
        1 => "One thing",
        2 => "Two things",
        3 => "Three things",
        4 => "Four things",
        5 => "Five things",
        6 => "Six things",
        7 => "Seven things",
        8 => "Eight things",
        9 => "Nine things",
        _ => $"{count} things",
    };

    private static string Missing(IReadOnlyList<CalendarSource> unavailable) =>
        unavailable.Count == 1
            ? $"I couldn’t reach your {Name(unavailable[0])} calendar."
            : "I couldn’t reach your calendars.";

    private static string Name(CalendarSource source) =>
        source is CalendarSource.Microsoft ? "Microsoft" : "Google";
}
