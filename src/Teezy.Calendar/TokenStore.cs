using System.Text.Json;
using System.Text.Json.Serialization;
using Teezy.Core.Abstractions;

namespace Teezy.Calendar;

/// <summary>Keeps each connected account's tokens where the API key already lives.</summary>
/// <remarks>
/// <para>
/// Over <see cref="ISecretStore"/> rather than <c>settings.json</c>, and not negotiably: a
/// refresh token is a standing key to a calendar that does not expire on its own, which makes
/// it more dangerous to leave in plain text than the API key that store was built for. On
/// Windows that means DPAPI — encrypted for this user on this machine, so a copied file is
/// worthless.
/// </para>
/// <para>
/// One secret per account rather than one document listing them all, so disconnecting an
/// account deletes its tokens outright instead of rewriting a file that still contains them.
/// </para>
/// </remarks>
public sealed class TokenStore(ISecretStore secrets)
{
    /// <summary>Namespaced so calendar tokens cannot collide with any other stored secret.</summary>
    private static string Key(string accountId) => $"calendar-{accountId}";

    public void Save(string accountId, OAuthTokens tokens) =>
        secrets.Write(Key(accountId), JsonSerializer.Serialize(new Stored(
            tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt)));

    /// <summary>What is stored, or null if nothing usable is.</summary>
    public OAuthTokens? Read(string accountId)
    {
        if (secrets.Read(Key(accountId)) is not { Length: > 0 } json) return null;

        try
        {
            var stored = JsonSerializer.Deserialize<Stored>(json);

            // A record with no refresh token is worse than none at all: it works for an hour
            // and then fails somewhere far from here. Treated as not connected, so the user is
            // asked to sign in again while the reason is still obvious.
            if (stored?.RefreshToken is not { Length: > 0 }) return null;

            return new OAuthTokens(stored.AccessToken ?? "", stored.RefreshToken, stored.ExpiresAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Delete(string accountId) => secrets.Delete(Key(accountId));

    private sealed record Stored(
        [property: JsonPropertyName("access")] string? AccessToken,
        [property: JsonPropertyName("refresh")] string? RefreshToken,
        [property: JsonPropertyName("expires")] DateTimeOffset ExpiresAt);
}
