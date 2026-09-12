using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Teezy.Calendar;

/// <summary>What the provider sent back to the redirect.</summary>
internal sealed record AuthResponse(string? Code, string? State, string? Error);

/// <summary>
/// A one-request web server on loopback, to catch the OAuth redirect.
/// </summary>
/// <remarks>
/// <para>
/// The standard desktop arrangement: the provider redirects the system browser to
/// <c>http://127.0.0.1:port/</c>, and the application reads the code from that one request. It
/// is the reason no password is ever typed into Teezy — the sign-in happens entirely on the
/// provider's own page, in the user's own browser.
/// </para>
/// <para>
/// <b>The port is taken from the OS, not chosen.</b> A hardcoded port collides with whatever
/// else happens to be running and fails at the worst moment; asking for port 0 and reading back
/// what was granted cannot.
/// </para>
/// <para>
/// <b>The host is the provider's choice, not ours.</b> <c>127.0.0.1</c> and <c>localhost</c>
/// are not interchangeable to redirect matching, and the two providers disagree: Microsoft
/// matches a registered <c>http://localhost</c> while ignoring the port, which is the only way
/// an OS-assigned port can work at all; Google documents the loopback literal and has
/// deprecated <c>localhost</c> for new clients. Registering the wrong one fails at sign-in with
/// a redirect mismatch and nothing else to go on.
/// </para>
/// </remarks>
internal sealed class LoopbackListener : IDisposable
{
    private readonly HttpListener _listener = new();

    internal string RedirectUri { get; }

    internal LoopbackListener(string host)
    {
        var port = FreePort();
        RedirectUri = $"http://{host}:{port}/";

        _listener.Prefixes.Add(RedirectUri);
        _listener.Start();
    }

    /// <summary>Waits for the browser to arrive, and tells the user they can close it.</summary>
    internal async Task<AuthResponse> WaitAsync(CancellationToken ct)
    {
        using var registration = ct.Register(() =>
        {
            // Stopping the listener is what unblocks GetContextAsync; there is no cancellable
            // overload, and leaving it waiting would hold the port for the life of the process.
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
        });

        HttpListenerContext context;
        try
        {
            context = await _listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
        {
            ct.ThrowIfCancellationRequested();
            throw new OAuthException("Sign-in was interrupted before it finished.", e);
        }

        var query = context.Request.QueryString;
        var response = new AuthResponse(query["code"], query["state"], query["error"]);

        await RespondAsync(context, response.Error is null).ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// A plain page saying it worked, because the browser is left showing whatever we send.
    /// </summary>
    /// <remarks>
    /// Deliberately self-contained and styleless: this is served from a random loopback port
    /// and must not fetch anything, and a tab that says nothing at all reads as a failure even
    /// when the sign-in succeeded.
    /// </remarks>
    private static async Task RespondAsync(HttpListenerContext context, bool ok)
    {
        var message = ok
            ? "<h2>Teezy is connected.</h2><p>You can close this tab.</p>"
            : "<h2>Sign-in was cancelled.</h2><p>You can close this tab.</p>";

        var body = Encoding.UTF8.GetBytes(
            "<!doctype html><meta charset=\"utf-8\"><title>Teezy</title>"
            + "<body style=\"font-family:system-ui;margin:4rem;color:#1B1A18\">"
            + message + "</body>");

        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = body.Length;

        await context.Response.OutputStream.WriteAsync(body).ConfigureAwait(false);
        context.Response.Close();
    }

    /// <summary>A port the OS says is free, by asking for any and reading back which.</summary>
    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    public void Dispose()
    {
        try { _listener.Close(); } catch (ObjectDisposedException) { }
    }
}
