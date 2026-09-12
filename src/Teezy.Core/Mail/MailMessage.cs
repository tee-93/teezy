namespace Teezy.Core.Mail;

/// <summary>Which account a message came from.</summary>
public enum MailSource
{
    Microsoft,
    Google,
}

/// <summary>One message, reduced to what an answer needs.</summary>
/// <param name="Subject">What the sender called it.</param>
/// <param name="FromName">The sender's display name, as they set it.</param>
/// <param name="FromAddress">The sender's address.</param>
/// <param name="Received">When it arrived, local time.</param>
/// <param name="Preview">The provider's short extract of the body. Often the useful part.</param>
/// <param name="IsUnread">Whether it is still unread.</param>
/// <param name="Source">Which mailbox, so two can be told apart in one answer.</param>
/// <remarks>
/// <para>
/// Thin on purpose, like <see cref="Calendar.CalendarEvent"/>, and for a sharper version of the
/// same reason. No full body, no attachments, no recipient lists, no headers. Every field that
/// exists here is a field that reaches a prompt, and a mailbox is a much larger surface than a
/// diary.
/// </para>
/// <para>
/// <b>The sender's address is kept although the display name is shown.</b> A display name is
/// chosen by whoever sent the message and is the single easiest thing to forge — "Origin
/// Energy" in the name with an address at some throwaway domain is what every phishing attempt
/// looks like. Any judgement about who a message is really from has to be made on the address.
/// </para>
/// <para>
/// <b>Every string here is hostile until proven otherwise.</b> Subjects, names and previews are
/// written by strangers, and a fair share of any mailbox is written by people actively trying
/// to manipulate whoever reads it. See <see cref="IMailbox"/> for the rule that makes showing
/// this to a model safe.
/// </para>
/// </remarks>
public sealed record MailMessage(
    string Subject,
    string FromName,
    string FromAddress,
    DateTimeOffset Received,
    string? Preview,
    bool IsUnread,
    MailSource Source)
{
    /// <summary>The part of the address after the @, lowercased. Empty if there isn't one.</summary>
    /// <remarks>
    /// The strongest signal about a sender that exists, and the one a bill detector will lean
    /// on rather than the display name.
    /// </remarks>
    public string Domain =>
        FromAddress.LastIndexOf('@') is var at && at >= 0 && at < FromAddress.Length - 1
            ? FromAddress[(at + 1)..].ToLowerInvariant()
            : string.Empty;

    /// <summary>What to call the sender out loud.</summary>
    /// <remarks>
    /// The display name when there is one, because "Origin Energy" is what a person recognises
    /// and reading an address aloud is miserable. This is for speaking, never for deciding.
    /// </remarks>
    public string Who => FromName.Trim() is { Length: > 0 } named ? named : FromAddress;
}
