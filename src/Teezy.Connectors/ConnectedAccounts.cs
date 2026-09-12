using System.Collections.Concurrent;
using Teezy.Core.Calendar;
using Teezy.Core.Mail;

namespace Teezy.Connectors;

/// <summary>Turns the saved list of accounts into calendars that can be read.</summary>
/// <remarks>
/// <para>
/// The join between settings, which record only which accounts exist, and the secret store,
/// which holds the credentials. Everything above this deals in <see cref="ICalendar"/> and
/// never sees a token.
/// </para>
/// <para>
/// <b>Sessions are cached per account.</b> A fresh one on every question would re-read the
/// store each time and, worse, lose the in-memory token — so a refresh would happen on every
/// ask rather than once an hour, which with Microsoft's rotating refresh tokens is exactly the
/// pattern that disconnects an account.
/// </para>
/// </remarks>
public sealed class ConnectedAccounts(TokenStore tokens, Func<string?> microsoftClientId)
{
    private readonly ConcurrentDictionary<string, AccountSession> _sessions = new();
    private readonly ConcurrentDictionary<string, ICalendar> _calendars = new();
    private readonly ConcurrentDictionary<string, IMailbox> _mailboxes = new();

    /// <summary>Whether a Microsoft account could be connected at all.</summary>
    /// <remarks>
    /// Nothing works without a registration to sign in to, and saying so up front is far
    /// kinder than a browser window that opens onto an error page.
    /// </remarks>
    public bool CanConnectMicrosoft => !string.IsNullOrWhiteSpace(microsoftClientId());

    /// <summary>Live calendars for the accounts given, in that order.</summary>
    public IReadOnlyList<ICalendar> Calendars(IReadOnlyList<ConnectedAccount> accounts)
    {
        Forget(accounts);
        return [.. accounts.Select(a => Session(a)).OfType<AccountSession>().Select(Calendar)];
    }

    /// <summary>Live mailboxes for the accounts given, in that order.</summary>
    /// <remarks>
    /// The same accounts and the same sessions as <see cref="Calendars"/>. Whether mail is read
    /// at all is the caller's decision — this only says what could be.
    /// </remarks>
    public IReadOnlyList<IMailbox> Mailboxes(IReadOnlyList<ConnectedAccount> accounts)
    {
        Forget(accounts);
        return [.. accounts.Select(a => Session(a)).OfType<AccountSession>().Select(Mailbox)];
    }

    /// <summary>Drops anything no longer in settings.</summary>
    /// <remarks>
    /// So a disconnected account stops being asked rather than lingering in the cache until
    /// the next launch.
    /// </remarks>
    private void Forget(IReadOnlyList<ConnectedAccount> accounts)
    {
        foreach (var stale in _sessions.Keys.Except(accounts.Select(a => a.Id)).ToList())
        {
            _sessions.TryRemove(stale, out _);
            _calendars.TryRemove(stale, out _);
            _mailboxes.TryRemove(stale, out _);
        }
    }

    /// <summary>
    /// The one session per account, which is the whole reason this class exists.
    /// </summary>
    /// <remarks>
    /// The calendar and the mailbox must share it. Microsoft rotates the refresh token on every
    /// use, so two sessions for one account would race to redeem the same token and revoke it —
    /// the failure would appear as an account that mysteriously signs itself out.
    /// </remarks>
    private AccountSession? Session(ConnectedAccount account)
    {
        if (account.Source is not CalendarSource.Microsoft) return null;

        // Google is not built yet. Returning null rather than throwing means a settings file
        // that mentions one — hand-edited, or written by a later version — still starts.
        if (microsoftClientId() is not { Length: > 0 } clientId) return null;

        return _sessions.GetOrAdd(
            account.Id,
            id => new AccountSession(GraphCalendar.Provider(clientId), id, tokens));
    }

    private ICalendar Calendar(AccountSession session) =>
        _calendars.GetOrAdd(session.AccountId, _ => new GraphCalendar(session));

    private IMailbox Mailbox(AccountSession session) =>
        _mailboxes.GetOrAdd(session.AccountId, _ => new GraphMailbox(session));

    /// <summary>Signs in to a Microsoft account and returns what to save.</summary>
    /// <exception cref="OAuthException">Declined, timed out, or the registration is wrong.</exception>
    public Task<ConnectedAccount> ConnectMicrosoftAsync(
        CalendarProfile profile, CancellationToken ct = default)
    {
        if (microsoftClientId() is not { Length: > 0 } clientId)
        {
            throw new OAuthException(
                "Teezy needs a Microsoft application id before it can sign in.");
        }

        return GraphCalendar.ConnectAsync(clientId, profile, tokens, ct: ct);
    }

    /// <summary>Forgets an account's credentials. The caller drops it from settings.</summary>
    /// <remarks>
    /// The tokens go first and unconditionally. A crash between the two leaves an orphaned
    /// secret rather than an account that is still readable after being removed from the UI.
    /// </remarks>
    public void Disconnect(ConnectedAccount account)
    {
        tokens.Delete(account.Id);
        _sessions.TryRemove(account.Id, out _);
        _calendars.TryRemove(account.Id, out _);
        _mailboxes.TryRemove(account.Id, out _);
    }
}
