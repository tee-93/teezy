namespace Teezy.Core.Calendar;

/// <summary>What a spoken calendar question is asking for.</summary>
public enum CalendarAsk
{
    /// <summary>Not a calendar question at all.</summary>
    None,

    /// <summary>"What's next", "what have I got coming up".</summary>
    Next,

    /// <summary>"What's on today", "what does my day look like".</summary>
    Today,

    /// <summary>"What's on tomorrow".</summary>
    Tomorrow,

    /// <summary>"Am I free this afternoon" — anything the patterns cannot answer alone.</summary>
    Other,
}

/// <summary>
/// Recognises the calendar questions worth answering without a round trip.
/// </summary>
/// <remarks>
/// <para>
/// The same two-tier bargain as commands: the handful of phrasings people actually use every
/// day are matched locally and answered from the events directly, instantly and for nothing.
/// Anything else is <see cref="CalendarAsk.Other"/> and goes to Claude with the events
/// attached — which costs money, takes a second, and is the only part that sends anything.
/// </para>
/// <para>
/// "What's next" is by some distance the most asked and the least deserving of an API call.
/// </para>
/// </remarks>
public static class CalendarQuestion
{
    public static CalendarAsk Classify(string? spoken)
    {
        var text = Commands.CommandMatcher.Normalise(spoken);
        if (text.Length == 0) return CalendarAsk.None;

        // Anchored like the command patterns, and for the same reason: an unanchored "meeting"
        // would claim dictation someone meant for a text box.
        if (!MentionsDiary(text)) return CalendarAsk.None;

        if (text.Contains("tomorrow", StringComparison.Ordinal)) return CalendarAsk.Tomorrow;

        if (text.Contains("next", StringComparison.Ordinal)
            || text.Contains("coming up", StringComparison.Ordinal))
        {
            return CalendarAsk.Next;
        }

        if (text.Contains("today", StringComparison.Ordinal)
            || text.Contains("rest of the day", StringComparison.Ordinal)
            || text.Contains("my day", StringComparison.Ordinal))
        {
            return CalendarAsk.Today;
        }

        return CalendarAsk.Other;
    }

    /// <summary>
    /// Whether this is about the diary at all.
    /// </summary>
    /// <remarks>
    /// A gate rather than a pattern, because the question forms are endless but the vocabulary
    /// is small. Getting this wrong in one direction sends ordinary questions to a calendar
    /// that cannot answer them; in the other it silently swallows them.
    /// </remarks>
    private static bool MentionsDiary(string text) =>
        text.Contains("calendar", StringComparison.Ordinal)
        || text.Contains("diary", StringComparison.Ordinal)
        || text.Contains("meeting", StringComparison.Ordinal)
        || text.Contains("schedule", StringComparison.Ordinal)
        || text.Contains("appointment", StringComparison.Ordinal)
        || text.Contains("am i free", StringComparison.Ordinal)
        || text.Contains("am i busy", StringComparison.Ordinal)
        || text.Contains("whats next", StringComparison.Ordinal)
        || text.Contains("what is next", StringComparison.Ordinal)
        || text.Contains("whats on", StringComparison.Ordinal)
        || text.Contains("what is on", StringComparison.Ordinal)
        || text.Contains("coming up", StringComparison.Ordinal);
}
