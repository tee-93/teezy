using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Teezy.Core.Calendar;

namespace Teezy.Connectors;

/// <summary>Reads a Google Calendar through the Calendar API.</summary>
/// <remarks>
/// <para>
/// The Google twin of <see cref="GraphCalendar"/>, and deliberately the same shape: same
/// <see cref="AccountSession"/>, same token store, same read-only posture. The differences are
/// all in the provider's own habits, and each one is noted where it bites.
/// </para>
/// <para>
/// Authorised with <c>calendar.readonly</c>. Nothing here can write.
/// </para>
/// </remarks>
public sealed class GoogleCalendar(AccountSession session, HttpClient? http = null) : ICalendar
{
    private const string Api = "https://www.googleapis.com/calendar/v3";

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly HttpClient _http = http ?? Shared;

    /// <summary>Where to send the user, and what to ask for.</summary>
    /// <param name="clientId">The Desktop client from the Google Cloud console.</param>
    /// <param name="clientSecret">
    /// Issued even for desktop clients, where it is not actually secret — the binary is on the
    /// user's machine and anyone can read it out. Google documents this and requires it anyway,
    /// so it is sent; PKCE is what actually protects the exchange.
    /// </param>
    public static OAuthProvider Provider(string clientId, string? clientSecret) => new(
        "https://accounts.google.com/o/oauth2/v2/auth",
        "https://oauth2.googleapis.com/token",
        clientId,
        clientSecret,

        // openid and email only so Settings can show which account this is. Google's calendar
        // read scope is "sensitive" rather than "restricted", so it needs no security
        // assessment — unlike Gmail, which is why mail here will not come this way.
        Scopes:
        [
            "openid",
            "email",
            "https://www.googleapis.com/auth/calendar.readonly",
        ],

        // Google documents the loopback literal and has deprecated localhost for new clients —
        // the opposite of Microsoft, which is why the host is per-provider.
        RedirectHost: "127.0.0.1");

    public CalendarSource Source => CalendarSource.Google;

    public bool IsConnected => session.IsConnected;

    public async Task<IReadOnlyList<CalendarEvent>> BetweenAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var url = $"{Api}/calendars/primary/events"
                  + $"?timeMin={Uri.EscapeDataString(Iso(from))}"
                  + $"&timeMax={Uri.EscapeDataString(Iso(to))}"

                  // The equivalent of Graph's calendarView: without it a weekly stand-up comes
                  // back once, as a rule to be expanded here rather than on the server. It is
                  // also required before orderBy=startTime is allowed.
                  + "&singleEvents=true"
                  + "&orderBy=startTime"

                  // Sized for the dashboard's whole week in one request, not one spoken question.
                  + "&maxResults=250";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await session.AccessTokenAsync(ct).ConfigureAwait(false));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new CalendarUnavailableException("Couldn’t reach your Google calendar.", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                throw new CalendarUnavailableException(
                    "Your Google account needs signing in again.", needsReconnect: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new CalendarUnavailableException(
                    $"Google wouldn’t give up the calendar: {Describe(body)}");
            }

            return Read(body);
        }
    }

    /// <summary>Signs in, stores the tokens, and reports who was signed in.</summary>
    public static async Task<ConnectedAccount> ConnectAsync(
        string clientId,
        string? clientSecret,
        CalendarProfile profile,
        TokenStore tokens,
        Func<string, Task>? openBrowser = null,
        CancellationToken ct = default)
    {
        var granted = await OAuthFlow
            .ConnectAsync(Provider(clientId, clientSecret), openBrowser, ct)
            .ConfigureAwait(false);

        if (granted.RefreshToken is not { Length: > 0 })
        {
            // Google returns one only on the first consent unless prompt=consent is sent, which
            // OAuthFlow does. Reaching here means something else is wrong, and saying so beats
            // an account that silently dies in an hour.
            throw new OAuthException(
                "Google didn’t issue a refresh token, so the connection would only last an "
                + "hour. Try disconnecting the app under your Google account's security "
                + "settings and connecting again.");
        }

        var id = ConnectedAccount.NewId();
        tokens.Save(id, granted);

        return new ConnectedAccount(
            id,
            await NameAsync(granted.AccessToken, ct).ConfigureAwait(false),
            CalendarSource.Google,
            profile);
    }

    /// <summary>Which account this is, for Settings to show. Best effort.</summary>
    private static async Task<string> NameAsync(string accessToken, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, "https://www.googleapis.com/oauth2/v3/userinfo");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            using var response = await Shared.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return "Google account";

            using var json = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));

            return Text(json.RootElement, "email")
                   ?? Text(json.RootElement, "name")
                   ?? "Google account";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return "Google account";
        }
    }

    // ---- reading the reply ----

    internal static IReadOnlyList<CalendarEvent> Read(string body)
    {
        List<CalendarEvent> events = [];

        try
        {
            using var json = JsonDocument.Parse(body);

            if (!json.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind is not JsonValueKind.Array)
            {
                return events;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (ReadEvent(item) is { } occurrence) events.Add(occurrence);
            }
        }
        catch (JsonException e)
        {
            throw new CalendarUnavailableException("Google sent back something unreadable.", e);
        }

        events.Sort((a, b) => a.Start.CompareTo(b.Start));

        return events;
    }

    private static CalendarEvent? ReadEvent(JsonElement item)
    {
        // Deleted occurrences of a recurring event stay in the list as tombstones. Reading one
        // as a meeting would put a cancelled stand-up back in the answer.
        if (Text(item, "status") is "cancelled") return null;

        if (!item.TryGetProperty("start", out var start)) return null;
        if (!item.TryGetProperty("end", out var end)) return null;

        // Google is clearer than Graph here: an all-day event carries "date" and a timed one
        // carries "dateTime", so the distinction needs no separate flag to be trusted.
        var allDay = Text(start, "date") is not null;

        if (When(start, allDay) is not { } from) return null;
        if (When(end, allDay) is not { } to) return null;

        return new CalendarEvent(
            Text(item, "summary") ?? "(no subject)",
            from,
            to,
            allDay,
            Text(item, "location"),
            CalendarSource.Google);
    }

    private static DateTimeOffset? When(JsonElement slot, bool allDay)
    {
        if (allDay)
        {
            if (Text(slot, "date") is not { } date) return null;

            if (!DateTime.TryParse(
                    date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            {
                return null;
            }

            // A date, not an instant. Anchored to local midnight so a birthday cannot drift to
            // the day before.
            return new DateTimeOffset(day.Date, TimeZoneInfo.Local.GetUtcOffset(day.Date));
        }

        if (Text(slot, "dateTime") is not { } text) return null;

        // RFC 3339 with the offset attached, unlike Graph's offset-less string plus a sibling
        // timezone field — so this one can be trusted as written.
        return DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when)
            ? when.ToLocalTime()
            : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>Google's own account of what went wrong.</summary>
    private static string Describe(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);

            if (json.RootElement.TryGetProperty("error", out var error))
            {
                // Sometimes an object with a message, sometimes a bare string.
                if (Text(error, "message") is { } message) return message;
                if (error.ValueKind is JsonValueKind.String) return error.GetString()!;
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
