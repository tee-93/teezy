namespace Teezy.Core.Mail;

/// <summary>What came back when the mail was read, including what did not.</summary>
/// <param name="Messages">Newest first, across every mailbox that answered.</param>
/// <param name="Unavailable">The mailboxes that could not be reached.</param>
public sealed record MailReading(
    IReadOnlyList<MailMessage> Messages,
    IReadOnlyList<MailSource> Unavailable)
{
    public static MailReading Empty { get; } = new([], []);

    /// <summary>Nothing answered at all, so this is a failure rather than an empty inbox.</summary>
    public bool NothingAnswered => Messages.Count == 0 && Unavailable.Count > 0;
}

/// <summary>Every connected mailbox, read as one inbox.</summary>
/// <remarks>
/// The mail twin of <see cref="Calendar.CombinedCalendar"/>, down to the partial-failure rule:
/// one mailbox failing is reported rather than hidden, because "no new email" said about a
/// mailbox nobody could read is how something important goes unnoticed.
/// </remarks>
/// <param name="mailboxes">
/// Asked each time rather than captured once, so connecting an account in Settings takes effect
/// on the next question instead of the next launch.
/// </param>
public sealed class CombinedMailbox(Func<IReadOnlyList<IMailbox>> mailboxes)
{
    /// <summary>A fixed set, for tests and anywhere the list genuinely cannot change.</summary>
    public CombinedMailbox(IReadOnlyList<IMailbox> mailboxes) : this(() => mailboxes) { }

    public bool IsConnected => mailboxes().Any(m => m.IsConnected);

    /// <summary>Reads every connected mailbox at once and merges what comes back.</summary>
    /// <param name="atMost">
    /// Applied per mailbox and again to the merged result, so two accounts cannot between them
    /// return twice what was asked for.
    /// </param>
    public async Task<MailReading> RecentAsync(
        DateTimeOffset since, int atMost, CancellationToken ct = default)
    {
        var connected = mailboxes().Where(m => m.IsConnected).ToList();
        if (connected.Count == 0) return MailReading.Empty;

        var readings = await Task
            .WhenAll(connected.Select(m => ReadOneAsync(m, since, atMost, ct)))
            .ConfigureAwait(false);

        List<MailMessage> messages = [];
        List<MailSource> unavailable = [];

        foreach (var (found, failed) in readings)
        {
            if (found is not null) messages.AddRange(found);
            if (failed is { } source && !unavailable.Contains(source)) unavailable.Add(source);
        }

        messages.Sort((a, b) => b.Received.CompareTo(a.Received));

        return new MailReading(
            messages.Count > atMost ? messages[..atMost] : messages,
            unavailable);
    }

    /// <summary>One mailbox's messages, or which one failed — never a thrown exception.</summary>
    private static async Task<(IReadOnlyList<MailMessage>? Found, MailSource? Failed)>
        ReadOneAsync(IMailbox mailbox, DateTimeOffset since, int atMost, CancellationToken ct)
    {
        try
        {
            return (await mailbox.RecentAsync(since, atMost, ct).ConfigureAwait(false), null);
        }
        catch (MailUnavailableException)
        {
            return (null, mailbox.Source);
        }
    }
}
