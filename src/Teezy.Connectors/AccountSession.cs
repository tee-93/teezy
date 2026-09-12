using Teezy.Core.Calendar;

namespace Teezy.Connectors;

/// <summary>Holds one connected account's tokens, and renews them when they lapse.</summary>
/// <remarks>
/// <para>
/// Written once and shared by both providers, because refreshing is the part that is identical
/// between them and the part that is easy to get subtly wrong twice. A calendar client should
/// ask for a token and get a working one; whether that involved a round trip to the provider is
/// not its problem.
/// </para>
/// <para>
/// <b>Refreshes are serialised.</b> Two questions asked in quick succession would otherwise both
/// find the token expired and both redeem the same refresh token. Providers that rotate refresh
/// tokens — Microsoft does — invalidate the first one when the second is used, so the race does
/// not merely waste a request, it can disconnect the account.
/// </para>
/// </remarks>
public sealed class AccountSession(OAuthProvider provider, string accountId, TokenStore tokens)
    : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OAuthTokens? _cached;

    public string AccountId { get; } = accountId;

    /// <summary>Whether there is anything stored worth trying.</summary>
    public bool IsConnected => _cached is not null || tokens.Read(AccountId) is not null;

    /// <summary>A token that is good right now, refreshing first if it is not.</summary>
    /// <exception cref="CalendarUnavailableException">
    /// The account is gone or the provider could not be reached.
    /// </exception>
    public async Task<string> AccessTokenAsync(CancellationToken ct = default)
    {
        var current = _cached ??= tokens.Read(AccountId);

        if (current is null)
        {
            throw new CalendarUnavailableException(
                "That account is not signed in.", needsReconnect: true);
        }

        if (!current.IsExpired) return current.AccessToken;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-read under the lock: whoever held it may have just refreshed, in which case
            // there is nothing left to do.
            current = _cached ??= tokens.Read(AccountId);
            if (current is null)
            {
                throw new CalendarUnavailableException(
                    "That account is not signed in.", needsReconnect: true);
            }

            if (!current.IsExpired) return current.AccessToken;

            return await RenewAsync(current, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> RenewAsync(OAuthTokens current, CancellationToken ct)
    {
        if (current.RefreshToken is not { Length: > 0 } refresh)
        {
            throw new CalendarUnavailableException(
                "That account needs signing in again.", needsReconnect: true);
        }

        OAuthTokens renewed;
        try
        {
            renewed = await OAuthFlow.RefreshAsync(provider, refresh, ct).ConfigureAwait(false);
        }
        catch (OAuthException e)
        {
            // A refused refresh is final — a revoked grant, a changed password, an account
            // removed. Retrying achieves nothing, so say so and stop rather than failing
            // quietly every time the calendar is asked.
            throw new CalendarUnavailableException(
                "That account needs signing in again.", e, needsReconnect: true);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new CalendarUnavailableException("Couldn’t reach the account to renew it.", e);
        }

        // Microsoft rotates the refresh token on every use and Google usually does not return
        // one at all here; keeping the old one when none came back is what makes both work.
        var kept = new OAuthTokens(
            renewed.AccessToken, renewed.RefreshToken ?? refresh, renewed.ExpiresAt);

        _cached = kept;
        tokens.Save(AccountId, kept);

        return kept.AccessToken;
    }

    /// <summary>Forgets the account, here and on disk.</summary>
    public void Disconnect()
    {
        _cached = null;
        tokens.Delete(AccountId);
    }

    public void Dispose() => _gate.Dispose();
}
