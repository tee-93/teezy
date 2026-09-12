using System.Text;

namespace Teezy.Core.Mail;

/// <summary>Turns an inbox into a sentence, without asking anyone.</summary>
/// <remarks>
/// <para>
/// The mail twin of <see cref="Calendar.CalendarAnswer"/>. "Any new email" is asked several
/// times a day, has one right answer, and should never cost a network round trip beyond the one
/// that fetched the mail.
/// </para>
/// <para>
/// <b>Senders are named, subjects are not.</b> Who wrote to you is the answer to "anything
/// new"; reading subject lines aloud turns a two-second check into a recital, and subject lines
/// are the field most often written to bait a reaction. Names come from the sender's display
/// name, which is also attacker-controlled — so this says who a message <i>claims</i> to be
/// from and never asserts anything about who it is really from.
/// </para>
/// </remarks>
public static class MailAnswer
{
    /// <summary>How many senders get named before the rest are counted.</summary>
    private const int NamedAtMost = 3;

    /// <summary>How far back each question looks, and how much it will carry.</summary>
    /// <remarks>
    /// A day for "today", a week for unread — an unread message from Tuesday is still unread,
    /// and an answer that ignored it would be wrong in the direction that matters.
    /// </remarks>
    public static (DateTimeOffset Since, int AtMost) Window(MailAsk ask, DateTimeOffset now) =>
        ask switch
        {
            MailAsk.Today => (Midnight(now), 50),
            _ => (now.AddDays(-7), 50),
        };

    /// <summary>The reply, or null if only the smarter tier can answer this one.</summary>
    public static string? For(MailAsk ask, MailReading reading, DateTimeOffset now)
    {
        if (reading.NothingAnswered) return Missing(reading.Unavailable);

        var answer = ask switch
        {
            MailAsk.Unread => Unread(reading.Messages),
            MailAsk.Today => Today(reading.Messages, now),
            _ => null,
        };

        if (answer is null) return null;

        return reading.Unavailable.Count == 0
            ? answer
            : $"{answer} {Missing(reading.Unavailable)}";
    }

    private static string Unread(IReadOnlyList<MailMessage> messages)
    {
        var unread = messages.Where(m => m.IsUnread).ToList();

        if (unread.Count == 0) return "Nothing unread.";

        return unread.Count == 1
            ? $"One unread, from {unread[0].Who}."
            : $"{unread.Count} unread, {Senders(unread)}.";
    }

    private static string Today(IReadOnlyList<MailMessage> messages, DateTimeOffset now)
    {
        var today = messages
            .Where(m => m.Received.ToLocalTime().Date == now.ToLocalTime().Date)
            .ToList();

        if (today.Count == 0) return "Nothing in today.";

        var unread = today.Count(m => m.IsUnread);

        var count = today.Count == 1 ? "One email today" : $"{today.Count} emails today";

        // The unread count is the part anyone actually wanted; the total on its own says
        // nothing, since a busy inbox is busy every day.
        var read = unread switch
        {
            0 => ", all read",
            _ when unread == today.Count => "",
            _ => $", {unread} unread",
        };

        return $"{count}{read}, {Senders(today)}.";
    }

    /// <summary>Who it is from, named where that is short enough to be useful.</summary>
    private static string Senders(IReadOnlyList<MailMessage> messages)
    {
        // Distinct, because six notifications from one sender is one fact, not six.
        var names = messages
            .Select(m => m.Who)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (names.Count == 1) return $"from {names[0]}";

        var named = names.Take(NamedAtMost).ToList();
        var rest = names.Count - named.Count;

        var line = new StringBuilder("from ");

        for (var i = 0; i < named.Count; i++)
        {
            if (i > 0) line.Append(i == named.Count - 1 && rest == 0 ? " and " : ", ");
            line.Append(named[i]);
        }

        if (rest > 0) line.Append($" and {rest} other{(rest == 1 ? "" : "s")}");

        return line.ToString();
    }

    /// <summary>The messages, written out for a model rather than for speech.</summary>
    /// <remarks>
    /// <para>
    /// <b>Subjects and previews go in, and they are the hostile part.</b> They have to: "has the
    /// electricity bill arrived" cannot be answered from sender names alone. This is exactly why
    /// the request they travel in carries no tools.
    /// </para>
    /// <para>
    /// The sender's address goes in alongside the display name, because the whole question of
    /// whether a message is really from the electricity company turns on the domain and never
    /// on the name.
    /// </para>
    /// </remarks>
    public static UntrustedMaterial Material(
        IReadOnlyList<MailMessage> messages, DateTimeOffset now)
    {
        var text = new StringBuilder();

        if (messages.Count == 0)
        {
            text.AppendLine("Their inbox has nothing in it over the period in question.");
            return new UntrustedMaterial(MaterialKind.Mail, text.ToString());
        }

        text.AppendLine("Their inbox, newest first:");

        foreach (var message in messages)
        {
            text.Append("- ")
                .Append(Spoken.LongDate(message.Received, now))
                .Append(' ')
                .Append(Spoken.Clock(message.Received))
                .Append(message.IsUnread ? " (unread)" : "")
                .Append(" from ")
                .Append(message.Who)
                .Append(" <")
                .Append(message.FromAddress)
                .Append(">: ")
                .AppendLine(message.Subject);

            if (message.Preview is { Length: > 0 } preview)
            {
                text.Append("  ").AppendLine(Flatten(preview));
            }
        }

        return new UntrustedMaterial(MaterialKind.Mail, text.ToString());
    }

    /// <summary>One line, trimmed.</summary>
    /// <remarks>
    /// Newlines are stripped so a preview cannot pose as new entries in the list above it — the
    /// cheapest form of the trick, and worth closing even though the request has no tools to
    /// reach.
    /// </remarks>
    private static string Flatten(string preview)
    {
        var flat = preview.ReplaceLineEndings(" ").Trim();
        return flat.Length <= 200 ? flat : flat[..200] + "…";
    }

    private static DateTimeOffset Midnight(DateTimeOffset now)
    {
        var today = now.ToLocalTime().Date;
        return new DateTimeOffset(today, TimeZoneInfo.Local.GetUtcOffset(today));
    }

    private static string Missing(IReadOnlyList<MailSource> unavailable) =>
        unavailable.Count == 1
            ? $"I couldn’t reach your {Name(unavailable[0])} mail."
            : "I couldn’t reach your mail.";

    private static string Name(MailSource source) =>
        source is MailSource.Microsoft ? "Microsoft" : "Gmail";
}
