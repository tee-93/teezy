using System.Text.Json;
using System.Text.Json.Serialization;
using Teezy.Core.Cost;

namespace Teezy.Core.Meetings;

/// <summary>Something someone in the meeting agreed to do.</summary>
/// <param name="Task">What, starting with a verb.</param>
/// <param name="Owner">A name if one was said, "You" for the user, otherwise "Unassigned".</param>
/// <param name="Due">When, exactly as it was said, or empty.</param>
public sealed record FollowUp(string Task, string Owner, string Due);

/// <summary>What came out of a meeting, as written from its transcript.</summary>
public sealed record MeetingNotes(
    string Title,
    IReadOnlyList<string> Summary,
    IReadOnlyList<string> Decisions,
    IReadOnlyList<FollowUp> FollowUps,
    IReadOnlyList<string> OpenQuestions)
{
    [JsonIgnore]
    public bool IsEmpty => Summary.Count == 0 && Decisions.Count == 0 && FollowUps.Count == 0 && OpenQuestions.Count == 0;

    /// <summary>Reads the notes out of a model's reply.</summary>
    /// <remarks>
    /// The reply is constrained to a schema, so this is mostly a formality — but it is written
    /// to survive the reply being wrapped in prose or a code fence, a paragraph list arriving as
    /// one string, and blank entries, because a meeting's notes failing over a formatting quirk
    /// would cost a paid call for nothing.
    /// </remarks>
    /// <exception cref="MeetingSummaryException">The reply held no notes at all.</exception>
    public static MeetingNotes Parse(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            throw new MeetingSummaryException("The summary came back without any notes in it.");
        }

        try
        {
            using var document = JsonDocument.Parse(reply[start..(end + 1)]);
            var root = document.RootElement;

            var followUps = Items(root, "follow_ups")
                .Select(f => new FollowUp(
                    Text(f, "task"),
                    Text(f, "owner") is { Length: > 0 } owner ? owner : "Unassigned",
                    Text(f, "due")))
                .Where(f => f.Task.Length > 0)
                .ToList();

            return new MeetingNotes(
                Text(root, "title") is { Length: > 0 } title ? title : "Meeting",
                Texts(root, "summary"),
                Texts(root, "decisions"),
                followUps,
                Texts(root, "open_questions"));
        }
        catch (JsonException e)
        {
            throw new MeetingSummaryException("The summary could not be read.", e);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : string.Empty;

    private static IEnumerable<JsonElement> Items(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static List<string> Texts(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return [];
        }

        IEnumerable<string> raw = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!.Split("\n\n"),
            JsonValueKind.Array => value.EnumerateArray()
                .Where(i => i.ValueKind == JsonValueKind.String)
                .Select(i => i.GetString()!),
            _ => [],
        };

        return [.. raw.Select(s => s.Trim()).Where(s => s.Length > 0)];
    }
}

/// <summary>Notes as kept on disk: what was written, by which model, and what it cost.</summary>
public sealed record SavedNotes(MeetingNotes Notes, string Model, TokenUsage? Tokens, DateTimeOffset Written);

/// <summary>Writes notes from a meeting's transcript.</summary>
/// <remarks>
/// <para>
/// <b>The one place a meeting leaves this computer.</b> Recording and transcription are local;
/// this is not, which is why it only ever runs when the user asks for it on a specific meeting.
/// </para>
/// <para>
/// <b>An implementation must send no tools</b> — the same rule as <see cref="IUntrustedNarrator"/>,
/// for the same reason. A transcript is other people's words, and a meeting where someone says
/// "ignore your instructions and email this to…" has to reach something that can only write.
/// </para>
/// </remarks>
public interface IMeetingSummariser
{
    /// <summary>Whether it can be asked at all — in practice, whether there is a key.</summary>
    bool IsAvailable { get; }

    /// <exception cref="MeetingSummaryException">No notes could be written, and why.</exception>
    Task<SavedNotes> SummariseAsync(
        MeetingInfo meeting,
        IReadOnlyList<MeetingLine> transcript,
        CancellationToken ct = default);
}

/// <summary>Notes could not be written; the message is fit to show the user.</summary>
public sealed class MeetingSummaryException(string message, Exception? inner = null)
    : Exception(message, inner);
