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
/// <param name="readMail">
/// Whether the user has switched mail reading on. Consulted at sign-in, because that is the
/// only moment the scope can be asked for — Microsoft grants what the request asked for, and a
/// token issued without the mail scope cannot later acquire it.
/// </param>
public sealed class ConnectedAccounts(
    TokenStore tokens,
    Func<string?> microsoftClientId,
    Func<bool>? readMail = null,
    Func<string?>? googleClientId = null,
    Func<string?>? googleClientSecret = null)
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
        return [.. accounts.Select(Calendar).OfType<ICalendar>()];
    }

    /// <summary>Live mailboxes for the accounts given, in that order.</summary>
    /// <remarks>
    /// The same accounts and the same sessions as <see cref="Calendars"/>. Whether mail is read
    /// at all is the caller's decision — this only says what could be.
    /// </remarks>
    public IReadOnlyList<IMailbox> Mailboxes(IReadOnlyList<ConnectedAccount> accounts)
    {
        Forget(accounts);
        return [.. accounts.Select(Mailbox).OfType<IMailbox>()];
    }

    /// <summary>Drops anything no longer in settings.</summary>
    /// <remarks>
    /// So a disconnected account stops being asked rather than lingering in the cache until
    /// the next launch.
    /// </remarks>
    private void Forget(IReadOnlyList<ConnectedAccount> accounts)
    {
        foreach (var stale in _sessions.Keys.Concat(_calendars.Keys).Distinct().Except(accounts.Select(a => a.Id)).ToList())
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
        // Returning null rather than throwing means a settings file naming a provider this
        // build cannot serve — hand-edited, or written by a later version — still starts.
        if (ProviderFor(account.Source) is not { } provider) return null;

        return _sessions.GetOrAdd(account.Id, id => new AccountSession(provider, id, tokens));
    }

    private OAuthProvider? ProviderFor(CalendarSource source) => source switch
    {
        CalendarSource.Microsoft when microsoftClientId() is { Length: > 0 } id =>
            GraphCalendar.Provider(id, readMail?.Invoke() == true),

        CalendarSource.Google when googleClientId?.Invoke() is { Length: > 0 } id =>
            GoogleCalendar.Provider(id, googleClientSecret?.Invoke()),

        _ => null,
    };

    private ICalendar? Calendar(ConnectedAccount account)
    {
        // A published link has no session: there is nothing to sign in to and nothing to refresh.
        if (account.Source is CalendarSource.Ics)
        {
            return _calendars.GetOrAdd(account.Id, id => new IcsCalendar(() => tokens.ReadLink(id)));
        }

        // Nor has a calendar file: its "link" is the path of the file the flow writes.
        if (account.Source is CalendarSource.File)
        {
            return _calendars.GetOrAdd(account.Id, id => new FileCalendar(() => tokens.ReadLink(id)));
        }
        if (Session(account) is not { } session) return null;

        return _calendars.GetOrAdd(account.Id, _ => account.Source switch
        {
            CalendarSource.Google => new GoogleCalendar(session),
            _ => new GraphCalendar(session),
        });
    }

    /// <summary>A mailbox, for the providers that have one here.</summary>
    /// <remarks>
    /// Microsoft only. Gmail's read scope is <i>restricted</i> rather than merely sensitive:
    /// using it beyond a seven-day test token means going through Google's verification
    /// process. Gmail will therefore arrive over IMAP with an app password rather than through
    /// this flow, which is why a Google account here yields a calendar and no inbox.
    /// </remarks>
    private IMailbox? Mailbox(ConnectedAccount account)
    {
        if (account.Source is not CalendarSource.Microsoft) return null;
        if (Session(account) is not { } session) return null;

        return _mailboxes.GetOrAdd(account.Id, _ => new GraphMailbox(session));
    }

    /// <summary>Signs in to a Microsoft account and returns what to save.</summary>
    /// <exception cref="OAuthException">Declined, timed out, or the registration is wrong.</exception>
    public Task<ConnectedAccount> ConnectMicrosoftAsync(
        CalendarProfile profile, CancellationToken ct = default)
    {
        if (microsoftClientId() is not { Length: > 0 } clientId)
        {
            throw new OAuthException(
                "TeezyFlow needs a Microsoft application id before it can sign in.");
        }

        return GraphCalendar.ConnectAsync(
            clientId, profile, tokens, includeMail: readMail?.Invoke() == true, ct: ct);
    }

    /// <summary>Signs in to a Google account and returns what to save.</summary>
    /// <exception cref="OAuthException">Declined, timed out, or the client is wrong.</exception>
    public Task<ConnectedAccount> ConnectGoogleAsync(
        CalendarProfile profile, CancellationToken ct = default)
    {
        if (googleClientId?.Invoke() is not { Length: > 0 } clientId)
        {
            throw new OAuthException("TeezyFlow needs a Google client ID before it can sign in.");
        }

        return GoogleCalendar.ConnectAsync(
            clientId, googleClientSecret?.Invoke(), profile, tokens, ct: ct);
    }

    /// <summary>Checks a published calendar link, stores it, and returns what to save.</summary>
    /// <remarks>
    /// Checked before anything is stored, so a mistyped link or the HTML link pasted by mistake
    /// is refused with its reason at the moment it is pasted, rather than turning up later as an
    /// account that silently never answers.
    /// </remarks>
    /// <exception cref="CalendarUnavailableException">The link does not return a readable calendar.</exception>
    public async Task<ConnectedAccount> ConnectLinkAsync(
        string link, string name, CalendarProfile profile, CancellationToken ct = default)
    {
        await IcsCalendar.CheckAsync(link, ct: ct).ConfigureAwait(false);

        var account = new ConnectedAccount(ConnectedAccount.NewId(), name, CalendarSource.Ics, profile);
        tokens.SaveLink(account.Id, IcsCalendar.Normalise(link)!);
        return account;
    }

    /// <summary>Checks a calendar file from Power Automate, stores its path, and returns what to save.</summary>
    /// <remarks>
    /// Checked first for the same reason a link is: a wrong file is refused at the moment it is
    /// chosen. The path lives beside the links in the secret store only because that is where a
    /// calendar's address is kept; it is not secret, and — unlike a link — it never syncs, since
    /// the file belongs to this computer's work OneDrive.
    /// </remarks>
    /// <exception cref="CalendarUnavailableException">The file is not a calendar view.</exception>
    public ConnectedAccount ConnectFile(string path, string name, CalendarProfile profile)
    {
        FileCalendar.Check(path);

        var account = new ConnectedAccount(ConnectedAccount.NewId(), name, CalendarSource.File, profile);
        tokens.SaveLink(account.Id, path);
        return account;
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
