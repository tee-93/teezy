using System.Text.Json;
using Anthropic.Models.Messages;
using Teezy.Core.Commands;

namespace Teezy.Assistant;

/// <summary>
/// The tool list Claude chooses from, and the translation back into typed commands.
/// </summary>
/// <remarks>
/// <para>
/// One tool per <see cref="VoiceCommand"/> and nothing else. The list is the security boundary:
/// a model that can only pick from here cannot ask for something destructive, because nothing
/// destructive is offered.
/// </para>
/// <para>
/// <b>Translation back is validating, not trusting.</b> An unknown tool name, a missing
/// argument or a number outside its range yields no command at all rather than a best guess.
/// The model is a source of suggestions, and suggestions get checked.
/// </para>
/// </remarks>
internal static class AssistantTools
{
    private static readonly JsonElement ObjectType = JsonSerializer.SerializeToElement("object");

    internal static IReadOnlyList<ToolUnion> All { get; } =
    [
        Define("launch_app",
            "Open an application, or bring it to the front if it is already running.",
            ("query", "string", "The application's name as the user said it, e.g. \"chrome\".")),

        Define("set_volume",
            "Set the system volume to an absolute percentage.",
            ("percent", "integer", "0 to 100.")),

        Define("adjust_volume",
            "Make the system volume louder or quieter by a relative amount.",
            ("delta", "integer", "Percentage points, negative to turn down. Use 10 or -10 unless the user asked for a bigger change.")),

        Define("set_mute",
            "Mute or unmute the system volume.",
            ("muted", "boolean", "True to mute, false to unmute.")),

        Define("media",
            "Send a media transport key to whatever is playing.",
            ("key", "string", "One of: play_pause, next, previous.")),

        Define("lock_screen", "Lock the PC."),
    ];

    private static ToolUnion Define(
        string name, string description, params (string Name, string Type, string Description)[] args)
    {
        var properties = new Dictionary<string, JsonElement>();
        foreach (var (argName, type, argDescription) in args)
        {
            properties[argName] = JsonSerializer.SerializeToElement(
                new Dictionary<string, string> { ["type"] = type, ["description"] = argDescription });
        }

        return new Tool
        {
            Name = name,
            Description = description,
            InputSchema = new InputSchema
            {
                Type = ObjectType,
                Properties = properties,
                Required = [.. args.Select(a => a.Name)],
            },
        };
    }

    /// <summary>The command a tool call names, or null if it named nothing we recognise.</summary>
    internal static VoiceCommand? ToCommand(string? name, IReadOnlyDictionary<string, JsonElement>? args)
    {
        args ??= new Dictionary<string, JsonElement>();

        return name switch
        {
            "launch_app" => Text(args, "query") is { Length: > 0 } query
                ? new VoiceCommand.LaunchApp(query)
                : null,

            "set_volume" => Number(args, "percent") is { } percent
                ? new VoiceCommand.SetVolume(Math.Clamp(percent, 0, 100))
                : null,

            // Clamped to something a person could have meant. A model deciding to turn the
            // volume up by two hundred is not going to be allowed to.
            "adjust_volume" => Number(args, "delta") is { } delta and not 0
                ? new VoiceCommand.AdjustVolume(Math.Clamp(delta, -50, 50))
                : null,

            "set_mute" => Boolean(args, "muted") is { } muted
                ? new VoiceCommand.Mute(muted)
                : null,

            "media" => Text(args, "key") switch
            {
                "next" => new VoiceCommand.Media(MediaKey.Next),
                "previous" => new VoiceCommand.Media(MediaKey.Previous),
                "play_pause" => new VoiceCommand.Media(MediaKey.PlayPause),
                _ => null,
            },

            "lock_screen" => new VoiceCommand.LockScreen(),

            // Includes the model inventing a tool, which is the case this exists for.
            _ => null,
        };
    }

    private static string? Text(IReadOnlyDictionary<string, JsonElement> args, string name) =>
        args.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static int? Number(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var i) => i,

            // Models sometimes quote numbers. Accepting that is not trusting them — the result
            // is still range-checked above.
            JsonValueKind.String when int.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool? Boolean(IReadOnlyDictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => null,
        };
    }
}
