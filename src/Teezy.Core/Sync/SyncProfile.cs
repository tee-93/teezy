using System.Text.Json;
using System.Text.Json.Nodes;

namespace Teezy.Core.Sync;

/// <summary>What travels between computers: settings, keys, the dictionary and tasks.</summary>
/// <param name="SavedAt">When it was written. The newest file wins.</param>
/// <param name="SavedBy">The computer that wrote it, so each one can say where its setup came from.</param>
/// <param name="Settings">The portable settings — see <see cref="TeezySettings.ToPortable"/>.</param>
/// <param name="Secrets">
/// Keys and passwords by the name the secret store files them under. Only the ones that exist:
/// an absent entry means "not set on the computer that saved this", and is left alone rather
/// than deleted, so a computer that never had a key cannot wipe it from the others.
/// </param>
/// <param name="Dictionary">The dictionary file's text, exactly as written.</param>
/// <param name="Tasks">
/// The task list as JSON. Unlike everything else it is merged task by task rather than replaced,
/// so tasks added on two computers while apart both survive. Null from a version without tasks.
/// </param>
public sealed record SyncProfile(
    DateTimeOffset SavedAt,
    string SavedBy,
    JsonObject Settings,
    IReadOnlyDictionary<string, string> Secrets,
    string? Dictionary,
    string? Tasks = null,
    string? Quotes = null)
{
    public const string FileName = "TeezyFlow sync.tfsync";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public string ToJson() => new JsonObject
    {
        ["savedAt"] = SavedAt.ToString("O"),
        ["savedBy"] = SavedBy,
        ["settings"] = Settings.DeepClone(),
        ["secrets"] = JsonSerializer.SerializeToNode(Secrets, Json),
        ["dictionary"] = Dictionary,
        ["tasks"] = Tasks,
        ["quotes"] = Quotes,
    }.ToJsonString(Json);

    /// <exception cref="SyncUnlockException">The contents are not a profile.</exception>
    public static SyncProfile FromJson(string json)
    {
        try
        {
            var root = JsonNode.Parse(json)!.AsObject();
            return new SyncProfile(
                DateTimeOffset.Parse((string)root["savedAt"]!, System.Globalization.CultureInfo.InvariantCulture),
                (string?)root["savedBy"] ?? "another computer",
                root["settings"]?.AsObject().DeepClone().AsObject() ?? [],
                root["secrets"]?.Deserialize<Dictionary<string, string>>(Json) ?? [],
                (string?)root["dictionary"],
                (string?)root["tasks"],
                (string?)root["quotes"]);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException
                                      or NullReferenceException)
        {
            throw new SyncUnlockException("The sync file opened, but its contents weren’t readable.");
        }
    }
}
