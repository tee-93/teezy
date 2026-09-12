using System.Text.RegularExpressions;

namespace Teezy.Core.Mail;

/// <summary>What a spoken mail question is asking for.</summary>
public enum MailAsk
{
    /// <summary>Not about mail at all.</summary>
    None,

    /// <summary>"Any new email", "have I got anything unread".</summary>
    Unread,

    /// <summary>"What's come in today", "any mail this morning".</summary>
    Today,

    /// <summary>"Has the electricity bill arrived" — anything the patterns cannot answer.</summary>
    Other,
}

/// <summary>Recognises the mail questions worth answering without a round trip.</summary>
/// <remarks>
/// <para>
/// The same two-tier bargain as commands and the diary. "Any new email" has one right answer
/// and is asked constantly; paying a second and a fraction of a penny to have a model count
/// unread messages would be silly. Anything else is <see cref="MailAsk.Other"/> and goes to
/// Claude with the messages attached — which costs money and is the only part that sends
/// anything anywhere.
/// </para>
/// <para>
/// The gate is narrower than the diary's. "Mail" and "message" are ordinary words that turn up
/// in dictation constantly, so they only count alongside something that makes the utterance a
/// question about the inbox.
/// </para>
/// </remarks>
public static partial class MailQuestion
{
    public static MailAsk Classify(string? spoken)
    {
        var text = Possessives(Commands.CommandMatcher.Normalise(spoken));
        if (text.Length == 0) return MailAsk.None;

        if (!MentionsMail(text)) return MailAsk.None;

        // Checked before "today", because "any unread from today" is still fundamentally a
        // question about what has not been read.
        if (text.Contains("unread", StringComparison.Ordinal)
            || text.Contains("new email", StringComparison.Ordinal)
            || text.Contains("new mail", StringComparison.Ordinal)
            || text.Contains("anything new", StringComparison.Ordinal))
        {
            return MailAsk.Unread;
        }

        if (text.Contains("today", StringComparison.Ordinal)
            || text.Contains("this morning", StringComparison.Ordinal)
            || text.Contains("this afternoon", StringComparison.Ordinal)
            || text.Contains("come in", StringComparison.Ordinal)
            || text.Contains("came in", StringComparison.Ordinal))
        {
            return MailAsk.Today;
        }

        return MailAsk.Other;
    }

    /// <summary>Whether this is about the inbox at all.</summary>
    /// <remarks>
    /// <para>
    /// Deliberately stricter than the diary's gate. "Email" and "inbox" are specific enough to
    /// stand alone; "mail" and "message" are not — "send him a message about the invoice" is
    /// ordinary dictation, and claiming it would swallow text someone meant for a text box.
    /// </para>
    /// <para>
    /// The cost of being too eager here is worse than for the calendar, because the fallback is
    /// a request carrying a slice of the user's mailbox.
    /// </para>
    /// </remarks>
    private static bool MentionsMail(string text) =>
        text.Contains("email", StringComparison.Ordinal)
        || text.Contains("inbox", StringComparison.Ordinal)
        || text.Contains("unread", StringComparison.Ordinal)
        || Vaguer().IsMatch(text);

    /// <summary>The weaker words, and only when the sentence is asking about them.</summary>
    [GeneratedRegex(
        @"\b(any|anything|what|whats|have i got|did i get|got any|read my|check my|new)\b[^.]{0,30}\b(mail|messages|post)\b")]
    private static partial Regex Vaguer();

    /// <summary>Puts back the "s" that normalising an apostrophe strands.</summary>
    /// <remarks>
    /// See <see cref="Calendar.CalendarQuestion"/>: <c>Normalise</c> turns every non-alphanumeric
    /// character into a space, so "what's" arrives as "what s". Written once there, repeated
    /// here rather than shared, because the two gates are free to diverge and a shared helper
    /// would quietly couple them.
    /// </remarks>
    private static string Possessives(string text) => StrandedS().Replace(text, "$1s");

    [GeneratedRegex(@"(\w) s\b")]
    private static partial Regex StrandedS();
}
