using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Teezy.Connectors;

/// <summary>Where a provider's OAuth endpoints live, and what to ask it for.</summary>
/// <param name="Authorize">The page the user is sent to.</param>
/// <param name="Token">Where codes and refresh tokens are exchanged.</param>
/// <param name="ClientId">The application the user registered.</param>
/// <param name="ClientSecret">
/// Google issues one even for desktop applications, where it is not actually secret — the
/// binary is on the user's machine. Microsoft correctly does not issue one for a public client.
/// </param>
/// <param name="Scopes">
/// <b>Read-only, always.</b> The guarantee that Teezy cannot move a meeting is a permission
/// that was never granted, not a code path that promises not to.
/// </param>
/// <param name="RedirectHost">
/// Which spelling of loopback this provider's redirect matching expects. See
/// <see cref="LoopbackListener"/>: the two providers want different ones, and guessing costs a
/// redirect mismatch at the far end of a sign-in.
/// </param>
public sealed record OAuthProvider(
    string Authorize,
    string Token,
    string ClientId,
    string? ClientSecret,
    IReadOnlyList<string> Scopes,
    string RedirectHost = "127.0.0.1");

/// <summary>What came back, and when it stops working.</summary>
public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// Treated as expired a minute early.
    /// </summary>
    /// <remarks>
    /// A token that expires while a request is in flight fails the request rather than
    /// refreshing, and the margin costs nothing.
    /// </remarks>
    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt.AddMinutes(-1);
}

/// <summary>
/// The authorization code flow with PKCE, over a loopback redirect.
/// </summary>
/// <remarks>
/// <para>
/// The flow desktop applications are supposed to use, and the only one that works without
/// asking the user to paste a code by hand. The browser is the system one, so the password is
/// typed into the provider's own page and never near Teezy — which is the entire point, and why
/// there is no field anywhere in this app that asks for a Microsoft or Google password.
/// </para>
/// <para>
/// <b>PKCE and <c>state</c> are both load-bearing.</b> The loopback listener is on the user's
/// own machine, where any other local process can also reach it: the verifier proves the code
/// is being redeemed by whoever asked for it, and <c>state</c> proves the response belongs to
/// the request we made rather than one someone else started.
/// </para>
/// </remarks>
public static class OAuthFlow
{
    /// <summary>Long enough to find a password, short enough not to hang forever.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(5);

    /// <summary>Sends the user to the provider and waits for them to come back.</summary>
    /// <exception cref="OAuthException">Declined, timed out, or refused by the provider.</exception>
    public static async Task<OAuthTokens> ConnectAsync(
        OAuthProvider provider, Func<string, Task>? openBrowser = null, CancellationToken ct = default)
    {
        var verifier = RandomUrlSafe(64);
        var challenge = Challenge(verifier);
        var state = RandomUrlSafe(32);

        using var listener = new LoopbackListener(provider.RedirectHost);

        var url = $"{provider.Authorize}"
                  + $"?client_id={Uri.EscapeDataString(provider.ClientId)}"
                  + "&response_type=code"
                  + $"&redirect_uri={Uri.EscapeDataString(listener.RedirectUri)}"
                  + $"&scope={Uri.EscapeDataString(string.Join(' ', provider.Scopes))}"
                  + $"&state={state}"
                  + $"&code_challenge={challenge}"
                  + "&code_challenge_method=S256"

                  // Both providers only hand back a refresh token when asked, and in Google's
                  // case only on the first consent — hence prompt=consent, so reconnecting an
                  // account that was disconnected does not silently yield a token that cannot
                  // be renewed.
                  + "&access_type=offline&prompt=consent";

        if (openBrowser is not null) await openBrowser(url).ConfigureAwait(false);
        else OpenSystemBrowser(url);

        using var patience = CancellationTokenSource.CreateLinkedTokenSource(ct);
        patience.CancelAfter(Patience);

        AuthResponse response;
        try
        {
            response = await listener.WaitAsync(patience.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Running out of patience is an ordinary outcome, not a fault: a wrong client id
            // leaves Microsoft showing an error page that never redirects, so nothing ever
            // arrives. Left as a raw cancellation it escaped every catch at the call site —
            // and an unhandled exception in an async void event handler takes the app with it.
            throw new OAuthException(
                "Teezy gave up waiting for the sign-in. If the browser showed an error rather "
                + "than a sign-in page, check the application id and the redirect URI.");
        }

        if (!string.Equals(response.State, state, StringComparison.Ordinal))
        {
            throw new OAuthException("That sign-in did not match the one Teezy started.");
        }

        if (response.Error is { Length: > 0 } declined)
        {
            throw new OAuthException($"Sign-in was refused: {declined}");
        }

        if (response.Code is not { Length: > 0 } code)
        {
            throw new OAuthException("Sign-in finished without returning a code.");
        }

        return await ExchangeAsync(provider, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = listener.RedirectUri,
            ["code_verifier"] = verifier,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Trades a refresh token for a fresh access token.</summary>
    public static Task<OAuthTokens> RefreshAsync(
        OAuthProvider provider, string refreshToken, CancellationToken ct = default) =>
        ExchangeAsync(provider, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
        }, ct);

    private static async Task<OAuthTokens> ExchangeAsync(
        OAuthProvider provider, Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = provider.ClientId;
        if (provider.ClientSecret is { Length: > 0 } secret) form["client_secret"] = secret;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        using var response = await http
            .PostAsync(provider.Token, new FormUrlEncodedContent(form), ct)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // The body names the fault — an expired refresh token reads very differently from a
            // wrong client id, and only one of them is the user's problem.
            throw new OAuthException($"The provider refused the sign-in: {Describe(body)}");
        }

        var token = JsonSerializer.Deserialize<TokenResponse>(body)
                    ?? throw new OAuthException("The provider returned a reply Teezy could not read.");

        if (token.AccessToken is not { Length: > 0 } access)
        {
            throw new OAuthException("The provider returned no access token.");
        }

        return new OAuthTokens(
            access,
            token.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn > 0 ? token.ExpiresIn : 3600));
    }

    /// <summary>The human-readable half of an OAuth error, which is the useful half.</summary>
    private static string Describe(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);

            var description = json.RootElement.TryGetProperty("error_description", out var d)
                ? d.GetString()
                : null;

            var code = json.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;

            return description ?? code ?? Shorten(body);
        }
        catch (JsonException)
        {
            return Shorten(body);
        }
    }

    private static string Shorten(string body) =>
        body.Length <= 200 ? body.Trim() : body[..200].Trim() + "…";

    private static void OpenSystemBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new OAuthException("Couldn’t open a browser to sign in.", e);
        }
    }

    // ---- PKCE ----

    private static string RandomUrlSafe(int bytes) =>
        Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Challenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }
}

/// <summary>Sign-in did not complete.</summary>
public sealed class OAuthException(string message, Exception? inner = null)
    : Exception(message, inner);
