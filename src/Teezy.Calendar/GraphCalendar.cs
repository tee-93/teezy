using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Teezy.Core.Calendar;

namespace Teezy.Calendar;

/// <summary>Reads an Outlook or Microsoft 365 calendar through Microsoft Graph.</summary>
/// <remarks>
/// <para>
/// Graph rather than COM automation of the Outlook desktop application, which was the other
/// option and is a dead end: new Outlook has no COM interface at all, and the old one only
/// answers while it happens to be running. Graph reads the same diary from the server whether
/// Outlook is open, closed, or not installed.
/// </para>
/// <para>
/// <b><c>calendarView</c> rather than <c>events</c>.</b> The <c>events</c> collection returns
/// recurring meetings as a single master with a recurrence rule attached, which would have to
/// be expanded here — badly, and differently from how Outlook expands it. <c>calendarView</c>
/// asks the server to do it, so a weekly stand-up appears on the day it actually falls.
/// </para>
/// <para>
/// Authorised with <c>Calendars.Read</c>. Nothing in this class can write, and nothing that
/// talks Teezy into trying could either — see <see cref="ICalendar"/>.
/// </para>
/// </remarks>
public sealed class GraphCalendar(AccountSession session, HttpClient? http = null) : ICalendar
{
    private const string Graph = "https://graph.microsoft.com/v1.0";

    /// <summary>The common endpoint, which accepts personal and work accounts alike.</summary>
    /// <remarks>
    /// <c>/common</c> rather than <c>/consumers</c>, so the same registration serves a work
    /// account later without a second one and a second client id.
    /// </remarks>
    private const string Authority = "https://login.microsoftonline.com/common/oauth2/v2.0";

    private readonly HttpClient _http = http ?? Shared;

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Where to send the user, and what to ask for.</summary>
    /// <remarks>
    /// A public client, so no secret: a desktop binary cannot keep one, and Microsoft correctly
    /// declines to issue one. PKCE is what proves the exchange is ours instead.
    /// </remarks>
    public static OAuthProvider Provider(string clientId) => new(
        $"{Authority}/authorize",
        $"{Authority}/token",
        clientId,
        ClientSecret: null,

        // Calendars.Read is the whole point; offline_access is what makes the connection last
        // beyond an hour; User.Read is only so Settings can show which account this is, and is
        // the permission every new registration already has.
        Scopes: ["offline_access", "Calendars.Read", "User.Read"],

        // Microsoft matches a registered http://localhost while ignoring the port, which is the
        // only arrangement an OS-assigned port can satisfy.
        RedirectHost: "localhost");

    public CalendarSource Source => CalendarSource.Microsoft;

    public bool IsConnected => session.IsConnected;

    public async Task<IReadOnlyList<CalendarEvent>> BetweenAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var url = $"{Graph}/me/calendarView"
                  + $"?startDateTime={Uri.EscapeDataString(Iso(from))}"
                  + $"&endDateTime={Uri.EscapeDataString(Iso(to))}"
                  + "&$select=subject,start,end,isAllDay,location"
                  + "&$orderby=start/dateTime"

                  // Graph pages at ten by default, which quietly truncates a busy day into a
                  // wrong answer. Fifty covers any window worth asking about out loud.
                  + "&$top=50";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await session.AccessTokenAsync(ct).ConfigureAwait(false));

        // Everything comes back in UTC, so the only conversion is the one below and there is no
        // per-event timezone to misread.
        request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new CalendarUnavailableException("Couldn’t reach your Microsoft calendar.", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                throw new CalendarUnavailableException(
                    "Your Microsoft account needs signing in again.", needsReconnect: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new CalendarUnavailableException(
                    $"Microsoft wouldn’t give up the calendar: {Describe(body)}");
            }

            return Read(body);
        }
    }

    /// <summary>Signs in, stores the tokens, and reports who was signed in.</summary>
    /// <exception cref="OAuthException">Declined, or the registration is wrong.</exception>
    public static async Task<ConnectedAccount> ConnectAsync(
        string clientId,
        CalendarProfile profile,
        TokenStore tokens,
        Func<string, Task>? openBrowser = null,
        CancellationToken ct = default)
    {
        var granted = await OAuthFlow
            .ConnectAsync(Provider(clientId), openBrowser, ct)
            .ConfigureAwait(false);

        // Without this the connection works for an hour and then fails at some unrelated
        // moment. Almost always a registration missing offline_access, which is worth naming
        // here rather than leaving to be discovered tomorrow.
        if (granted.RefreshToken is not { Length: > 0 })
        {
            throw new OAuthException(
                "Microsoft didn’t issue a refresh token, so the connection would only last an "
                + "hour. Check the app registration includes the offline_access permission.");
        }

        var id = ConnectedAccount.NewId();
        tokens.Save(id, granted);

        var name = await NameAsync(granted.AccessToken, ct).ConfigureAwait(false);

        return new ConnectedAccount(id, name, CalendarSource.Microsoft, profile);
    }

    /// <summary>Which account this is, for Settings to show.</summary>
    /// <remarks>
    /// Best effort. Failing to read a display name is no reason to throw away a sign-in that
    /// worked, so an unnamed account is still a connected one.
    /// </remarks>
    private static async Task<string> NameAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{Graph}/me?$select=mail,userPrincipalName,displayName");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await Shared.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return "Microsoft account";

            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            return Text(json.RootElement, "mail")
                   ?? Text(json.RootElement, "userPrincipalName")
                   ?? Text(json.RootElement, "displayName")
                   ?? "Microsoft account";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "Microsoft account";
        }
    }

    // ---- reading the reply ----

    internal static IReadOnlyList<CalendarEvent> Read(string body)
    {
        List<CalendarEvent> events = [];

        try
        {
            using var json = JsonDocument.Parse(body);

            if (!json.RootElement.TryGetProperty("value", out var value)
                || value.ValueKind is not JsonValueKind.Array)
            {
                return events;
            }

            foreach (var item in value.EnumerateArray())
            {
                if (ReadEvent(item) is { } occurrence) events.Add(occurrence);
            }
        }
        catch (JsonException e)
        {
            throw new CalendarUnavailableException("Microsoft sent back something unreadable.", e);
        }

        // Ordered here as well as in the query, because the window is later merged with another
        // account's and the combined list has to be right regardless of what each server did.
        events.Sort((a, b) => a.Start.CompareTo(b.Start));

        return events;
    }

    private static CalendarEvent? ReadEvent(JsonElement item)
    {
        if (!item.TryGetProperty("start", out var start)) return null;
        if (!item.TryGetProperty("end", out var end)) return null;

        var allDay = item.TryGetProperty("isAllDay", out var flag)
                     && flag.ValueKind is JsonValueKind.True;

        if (When(start, allDay) is not { } from) return null;
        if (When(end, allDay) is not { } to) return null;

        var location = item.TryGetProperty("location", out var place)
            ? Text(place, "displayName")
            : null;

        return new CalendarEvent(
            Text(item, "subject") ?? "(no subject)",
            from,
            to,
            allDay,
            location,
            CalendarSource.Microsoft);
    }

    /// <summary>Turns one Graph date slot into a local time.</summary>
    /// <remarks>
    /// Graph writes <c>dateTime</c> with no offset on it and names the zone separately; the
    /// request asked for UTC, so that is what this is. All-day events are the exception and are
    /// deliberately not converted — they are a date, not an instant, and shifting one by a few
    /// hours moves a birthday to the wrong day.
    /// </remarks>
    private static DateTimeOffset? When(JsonElement slot, bool allDay)
    {
        if (Text(slot, "dateTime") is not { } text) return null;

        if (!DateTime.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        if (allDay)
        {
            var midnight = parsed.Date;
            return new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
        }

        return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc)).ToLocalTime();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>Graph's own account of what went wrong, which is usually the useful part.</summary>
    private static string Describe(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);

            if (json.RootElement.TryGetProperty("error", out var error)
                && Text(error, "message") is { } message)
            {
                return message;
            }
        }
        catch (JsonException)
        {
            // Fall through to the raw body, trimmed.
        }

        return body.Length <= 200 ? body.Trim() : body[..200].Trim() + "…";
    }

    private static string Iso(DateTimeOffset when) =>
        when.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
