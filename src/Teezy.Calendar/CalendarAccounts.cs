using System.Collections.Concurrent;
using Teezy.Core.Calendar;

namespace Teezy.Calendar;

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
public sealed class CalendarAccounts(TokenStore tokens, Func<string?> microsoftClientId)
{
    private readonly ConcurrentDictionary<string, ICalendar> _open = new();

    /// <summary>Whether a Microsoft account could be connected at all.</summary>
    /// <remarks>
    /// Nothing works without a registration to sign in to, and saying so up front is far
    /// kinder than a browser window that opens onto an error page.
    /// </remarks>
    public bool CanConnectMicrosoft => !string.IsNullOrWhiteSpace(microsoftClientId());

    /// <summary>Live calendars for the accounts given, in that order.</summary>
    public IReadOnlyList<ICalendar> Open(IReadOnlyList<ConnectedAccount> accounts)
    {
        // Anything no longer in settings is dropped, so a disconnected account stops being
        // asked rather than lingering in the cache until the next launch.
        foreach (var stale in _open.Keys.Except(accounts.Select(a => a.Id)).ToList())
        {
            _open.TryRemove(stale, out _);
        }

        return [.. accounts.Select(For).OfType<ICalendar>()];
    }

    private ICalendar? For(ConnectedAccount account)
    {
        if (account.Source is not CalendarSource.Microsoft) return null;

        // Google is not built yet. Returning null rather than throwing means a settings file
        // that mentions one — hand-edited, or written by a later version — still starts.
        if (microsoftClientId() is not { Length: > 0 } clientId) return null;

        return _open.GetOrAdd(account.Id, id => new GraphCalendar(
            new AccountSession(GraphCalendar.Provider(clientId), id, tokens)));
    }

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
        _open.TryRemove(account.Id, out _);
    }
}
