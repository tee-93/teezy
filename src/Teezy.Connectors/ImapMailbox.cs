using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Teezy.Core.Mail;

namespace Teezy.Connectors;

/// <summary>Reads a Gmail inbox over IMAP, with an app password.</summary>
/// <remarks>
/// <para>
/// <b>Why not the Gmail API.</b> Google classes <c>gmail.readonly</c> as a <i>restricted</i>
/// scope — its harshest tier. Using it beyond a seven-day test token means going through
/// Google's OAuth verification, and a security assessment if the data is ever stored on a
/// server. For one person reading their own mail on their own machine, that is an absurd
/// amount of process. An app password needs no Cloud project, no consent screen and no review.
/// </para>
/// <para>
/// <b>What that costs, stated plainly.</b> An app password is not scoped: it is full IMAP
/// access to the mailbox, not read-only, and Google will not issue a narrower one. The
/// read-only guarantee here is therefore <i>this code</i> rather than a permission — weaker
/// than the Microsoft side, where the provider enforces it. What holds it up is that nothing
/// in this class opens a writable folder, sets a flag, or deletes anything, and that the
/// connection is opened <see cref="FolderAccess.ReadOnly"/> so the server itself refuses
/// changes for the life of the session.
/// </para>
/// <para>
/// <b>Messages are not marked as read.</b> ReadOnly access is what guarantees it: a plain IMAP
/// fetch would otherwise set <c>\Seen</c> and quietly destroy the very signal "any new email"
/// depends on.
/// </para>
/// <para>
/// See <see cref="IMailbox"/> for the rule about what may be done with the text this returns.
/// </para>
/// </remarks>
public sealed class ImapMailbox(
    Func<string?> address, Func<string?> appPassword, string host = "imap.gmail.com")
    : IMailbox
{
    /// <summary>How long to allow for connect, authenticate and fetch together.</summary>
    /// <remarks>
    /// Generous compared with the REST providers because IMAP is chattier — a connect, a TLS
    /// handshake, a login and a fetch — and this sits behind a spoken question, so the
    /// alternative to waiting is an answer that never comes.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(25);

    public MailSource Source => MailSource.Google;

    public bool IsConnected =>
        !string.IsNullOrWhiteSpace(address()) && !string.IsNullOrWhiteSpace(appPassword());

    public async Task<IReadOnlyList<MailMessage>> RecentAsync(
        DateTimeOffset since, int atMost, CancellationToken ct = default)
    {
        if (address() is not { Length: > 0 } user || appPassword() is not { Length: > 0 } secret)
        {
            throw new MailUnavailableException(
                "Gmail is not set up.", needsReconnect: true);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Patience);

        using var client = new ImapClient();

        try
        {
            await client.ConnectAsync(host, 993, SecureSocketOptions.SslOnConnect, deadline.Token)
                .ConfigureAwait(false);

            await client.AuthenticateAsync(user, secret, deadline.Token).ConfigureAwait(false);

            // ReadOnly is load-bearing, not tidiness: opened read-write, fetching a message
            // sets \Seen and the unread flag this feature reports on is gone.
            await client.Inbox.OpenAsync(FolderAccess.ReadOnly, deadline.Token)
                .ConfigureAwait(false);

            return await FetchAsync(client.Inbox, since, atMost, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (AuthenticationException e)
        {
            // The likeliest failure by far, and it has one cause worth naming: an ordinary
            // account password will not work here, and neither will an app password after the
            // account's two-step verification is turned off.
            throw new MailUnavailableException(
                "Gmail refused the sign-in. App passwords only work with two-step verification "
                + "switched on, and an ordinary account password will not do.",
                e,
                needsReconnect: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new MailUnavailableException("Gmail took too long to answer.");
        }
        catch (Exception e) when (e is ImapProtocolException or ImapCommandException or IOException
                                      or System.Net.Sockets.SocketException)
        {
            throw new MailUnavailableException("Couldn’t reach Gmail.", e);
        }
        finally
        {
            if (client.IsConnected)
            {
                try
                {
                    await client.DisconnectAsync(true, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or ImapProtocolException)
                {
                    // A polite goodbye that fails changes nothing; the answer is already read.
                }
            }
        }
    }

    private static async Task<IReadOnlyList<MailMessage>> FetchAsync(
        IMailFolder inbox, DateTimeOffset since, int atMost, CancellationToken ct)
    {
        // Filtered at the server. Asking for everything and discarding locally would drag a
        // whole mailbox over the wire to answer "anything new this morning".
        var found = await inbox
            .SearchAsync(SearchQuery.DeliveredAfter(since.UtcDateTime.Date.AddDays(-1)), ct)
            .ConfigureAwait(false);

        if (found.Count == 0) return [];

        // Newest first, then capped, so a busy mailbox costs one small fetch rather than a
        // large one that is then thrown away.
        var wanted = found.Reverse().Take(atMost).ToList();

        var summaries = await inbox
            .FetchAsync(wanted, MessageSummaryItems.Envelope | MessageSummaryItems.Flags
                                | MessageSummaryItems.PreviewText, ct)
            .ConfigureAwait(false);

        List<MailMessage> messages = [];

        foreach (var summary in summaries)
        {
            if (Read(summary) is { } message && message.Received >= since) messages.Add(message);
        }

        messages.Sort((a, b) => b.Received.CompareTo(a.Received));

        return messages;
    }

    /// <summary>One IMAP summary as a message, or null if it is unusable.</summary>
    internal static MailMessage? Read(IMessageSummary summary)
    {
        if (summary.Envelope is not { } envelope) return null;

        var from = envelope.From.Mailboxes.FirstOrDefault();

        return new MailMessage(
            envelope.Subject ?? "(no subject)",
            from?.Name ?? "",
            from?.Address ?? "",
            (envelope.Date ?? summary.InternalDate ?? DateTimeOffset.MinValue).ToLocalTime(),
            summary.PreviewText,

            // Absent flags mean the server did not say, and something undescribed should not be
            // announced as new — the same rule as the Microsoft side.
            summary.Flags is { } flags && !flags.HasFlag(MessageFlags.Seen),
            MailSource.Google);
    }
}
