using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Teezy.Core.Mail;

namespace Teezy.Calendar;

/// <summary>Reads an Outlook or Microsoft 365 inbox through Microsoft Graph.</summary>
/// <remarks>
/// <para>
/// Sits in this project rather than a new one because it shares the whole sign-in half with
/// <see cref="GraphCalendar"/> — the same registration, the same <see cref="AccountSession"/>,
/// the same rotating refresh token. Splitting it out would mean two sessions refreshing the
/// same account, which is precisely the race that disconnects it.
/// </para>
/// <para>
/// Authorised with <c>Mail.Read</c>. Nothing here can send, delete, move or even mark a message
/// as read — and marking as read matters: an assistant that quietly cleared the unread flag on
/// everything it looked at would destroy the signal the user relies on.
/// </para>
/// <para>
/// See <see cref="IMailbox"/> for the rule that makes showing any of this to a model safe.
/// </para>
/// </remarks>
public sealed class GraphMailbox(AccountSession session, HttpClient? http = null) : IMailbox
{
    private const string Graph = "https://graph.microsoft.com/v1.0";

    private static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly HttpClient _http = http ?? Shared;

    public MailSource Source => MailSource.Microsoft;

    public bool IsConnected => session.IsConnected;

    public async Task<IReadOnlyList<MailMessage>> RecentAsync(
        DateTimeOffset since, int atMost, CancellationToken ct = default)
    {
        // The inbox specifically, not /me/messages — the latter includes Sent, Drafts and
        // Deleted, and "what came in today" would cheerfully report the user's own replies.
        var url = $"{Graph}/me/mailFolders/inbox/messages"
                  + "?$select=subject,from,receivedDateTime,bodyPreview,isRead"
                  + $"&$filter=receivedDateTime ge {Iso(since)}"
                  + "&$orderby=receivedDateTime desc"
                  + $"&$top={Math.Clamp(atMost, 1, 100)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await TokenAsync(ct).ConfigureAwait(false));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new MailUnavailableException("Couldn’t reach your Microsoft mail.", e);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                throw new MailUnavailableException(
                    "Your Microsoft account needs signing in again.", needsReconnect: true);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden)
            {
                // Distinct from a dead token, and the likeliest failure the first time: the
                // registration has Calendars.Read but nobody ticked Mail.Read.
                throw new MailUnavailableException(
                    "Teezy isn’t allowed to read this mailbox. The app registration needs the "
                    + "Mail.Read permission, and you have to sign in again after adding it.",
                    needsReconnect: true);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new MailUnavailableException(
                    $"Microsoft wouldn’t give up the mail: {Describe(body)}");
            }

            return Read(body);
        }
    }

    /// <summary>The access token, with the calendar's failure translated into mail's.</summary>
    private async Task<string> TokenAsync(CancellationToken ct)
    {
        try
        {
            return await session.AccessTokenAsync(ct).ConfigureAwait(false);
        }
        catch (Core.Calendar.CalendarUnavailableException e)
        {
            // The session is shared with the calendar and speaks its language. A mail question
            // must not answer with the word "calendar" in it.
            throw new MailUnavailableException(e.Message, e, e.NeedsReconnect);
        }
    }

    internal static IReadOnlyList<MailMessage> Read(string body)
    {
        List<MailMessage> messages = [];

        try
        {
            using var json = JsonDocument.Parse(body);

            if (!json.RootElement.TryGetProperty("value", out var value)
                || value.ValueKind is not JsonValueKind.Array)
            {
                return messages;
            }

            foreach (var item in value.EnumerateArray())
            {
                if (ReadOne(item) is { } message) messages.Add(message);
            }
        }
        catch (JsonException e)
        {
            throw new MailUnavailableException("Microsoft sent back something unreadable.", e);
        }

        messages.Sort((a, b) => b.Received.CompareTo(a.Received));

        return messages;
    }

    private static MailMessage? ReadOne(JsonElement item)
    {
        if (Text(item, "receivedDateTime") is not { } when) return null;

        if (!DateTimeOffset.TryParse(
                when, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var received))
        {
            return null;
        }

        var from = item.TryGetProperty("from", out var envelope)
                   && envelope.TryGetProperty("emailAddress", out var who)
            ? who
            : default;

        return new MailMessage(
            Text(item, "subject") ?? "(no subject)",
            from.ValueKind is JsonValueKind.Object ? Text(from, "name") ?? "" : "",
            from.ValueKind is JsonValueKind.Object ? Text(from, "address") ?? "" : "",
            received.ToLocalTime(),
            Text(item, "bodyPreview"),

            // Absent means read: a message Graph declines to describe should not be announced
            // as something new.
            item.TryGetProperty("isRead", out var read) && read.ValueKind is JsonValueKind.False,
            MailSource.Microsoft);
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

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
