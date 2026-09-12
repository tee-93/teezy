using Teezy.Core.Calendar;
using Teezy.Core.Mail;

namespace Teezy.Core;

/// <summary>The one line at the top of the dashboard.</summary>
/// <remarks>
/// <para>
/// The page opens with the assistant saying the day, rather than with a wall of tiles for the
/// reader to assemble into a sentence themselves. It is the same material the spoken answers
/// use, said shorter — so the window and the pill agree about what is going on.
/// </para>
/// <para>
/// <b>Two clauses at most.</b> This is read at a glance on the way to something else; a third
/// clause turns it into a paragraph and it stops being read at all.
/// </para>
/// </remarks>
public static class DaySummary
{
    /// <summary>What to say, given whatever could be read.</summary>
    /// <param name="diary">Today's events, or null when no calendar is connected.</param>
    /// <param name="mail">Recent messages, or null when no mailbox is connected or mail is off.</param>
    public static string For(CalendarReading? diary, MailReading? mail, DateTimeOffset now)
    {
        List<string> clauses = [];

        if (Diary(diary, now) is { } day) clauses.Add(day);
        if (Inbox(mail) is { } inbox) clauses.Add(inbox);

        var missing = Missing(diary, mail);

        if (clauses.Count == 0)
        {
            // Two different silences. Nothing connected is not a failure — most of Teezy has
            // never needed an account — so it says what to do. Everything connected failing is
            // a failure, and saying "ask me something" over the top of it would bury the one
            // fact the reader needs.
            return missing ?? "Hold your assistant key and ask me something.";
        }

        var said = clauses.Count == 1
            ? Sentence(clauses[0])
            : $"{Sentence(clauses[0])[..^1]}, and {clauses[1]}.";

        return missing is null ? said : $"{said} {missing}";
    }

    private static string? Diary(CalendarReading? diary, DateTimeOffset now)
    {
        if (diary is null) return null;

        // An unreachable calendar must never be summarised as a free afternoon; the caller is
        // told separately by Missing, and this clause simply stands aside.
        if (diary.NothingAnswered) return null;

        var left = diary.Events.Count(e => e.IsAllDay || e.End > now);

        return left switch
        {
            0 => "nothing else on today",
            1 => "one thing left today",
            _ => $"{Word(left)} things left today",
        };
    }

    private static string? Inbox(MailReading? mail)
    {
        if (mail is null || mail.NothingAnswered) return null;

        var unread = mail.Messages.Count(m => m.IsUnread);

        return unread switch
        {
            0 => "nothing unread",
            1 => "one unread",
            _ => $"{Word(unread)} unread",
        };
    }

    /// <summary>Which accounts could not be reached, if any.</summary>
    /// <remarks>
    /// Said as its own sentence rather than folded into the summary. "Two things left today"
    /// alongside a calendar that failed is a half-truth, and the reader has to be told which
    /// half — quietly dropping the account is how someone misses a meeting.
    /// </remarks>
    private static string? Missing(CalendarReading? diary, MailReading? mail)
    {
        var unreachable = (diary?.Unavailable.Count ?? 0) + (mail?.Unavailable.Count ?? 0);

        return unreachable switch
        {
            0 => null,
            1 => "One account couldn’t be reached.",
            _ => $"{unreachable} accounts couldn’t be reached.",
        };
    }

    private static string Sentence(string clause) =>
        char.ToUpperInvariant(clause[0]) + clause[1..] + ".";

    /// <summary>Small numbers in words, because the line is meant to be read as speech.</summary>
    private static string Word(int count) => count switch
    {
        2 => "two",
        3 => "three",
        4 => "four",
        5 => "five",
        6 => "six",
        7 => "seven",
        8 => "eight",
        9 => "nine",
        _ => count.ToString(System.Globalization.CultureInfo.CurrentCulture),
    };
}
