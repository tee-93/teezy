using Teezy.Core.Hotkeys;
using System.Text.Json;
using System.Text.Json.Serialization;
using Teezy.Core.Abstractions;

namespace Teezy.Core;

/// <summary>User settings, persisted as JSON next to the model and dictionary.</summary>
public sealed record TeezySettings
{
    /// <summary>The push-to-talk combination. Every key must be held together.</summary>
    public Hotkey Hotkey { get; init; } = Hotkey.Default;

    /// <summary>
    /// The single key this used to be, read only so old settings files still work.
    /// </summary>
    /// <remarks>
    /// Migrated in <see cref="Load"/> and then dropped: it is never written back, so the
    /// file converts itself the first time settings are saved. Kept rather than ignored
    /// because silently resetting someone's hotkey to the default would be worse than any
    /// amount of migration code.
    /// </remarks>
    [JsonPropertyName("PushToTalkKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyPushToTalkKey { get; init; }

    /// <summary>Run the deterministic cleanup pass. The dictionary runs either way.</summary>
    public bool CleanupEnabled { get; init; } = true;

    /// <summary>Play a short tone when recording starts and stops.</summary>
    public bool SoundEnabled { get; init; } = true;

    /// <summary>Show the floating level meter while recording.</summary>
    public bool ShowHud { get; init; } = true;

    /// <summary>
    /// Inference threads. Four measured fastest on this hardware; eight measured
    /// <i>slower</i>, so this is a tuned value rather than "more is better".
    /// </summary>
    public int NumThreads { get; init; } = 4;

    /// <summary>Override the model directory. Null means the default location.</summary>
    public string? ModelPath { get; init; }

    /// <summary>How the recogniser decodes. Takes effect when the model is next loaded.</summary>
    /// <remarks>
    /// Greedy by default: it is faster, and it is what every measurement in the README was
    /// taken against. Beam search is the setting to reach for when the engine keeps missing
    /// unusual words — and it is required for dictionary hints to do anything at all.
    /// </remarks>
    public Abstractions.DecodingMethod Decoding { get; init; } = Abstractions.DecodingMethod.Greedy;

    /// <summary>Candidates beam search keeps alive. Ignored under greedy decoding.</summary>
    public int BeamSize { get; init; } = 4;

    /// <summary>How hard dictionary hints pull the recogniser toward their spelling.</summary>
    public double HotwordScore { get; init; } = 1.5;

    /// <summary>Send the cleaned text to Claude for a second, smarter pass.</summary>
    /// <remarks>
    /// Off by default, and off is the honest default: switching it on ends the guarantee that
    /// nothing leaves the machine, needs a paid API account, and adds a network round trip to
    /// every utterance. The API key is <b>not</b> stored here - see <c>ISecretStore</c>.
    /// </remarks>
    public bool LlmCleanupEnabled { get; init; }

    /// <summary>Which Claude model does the cleanup pass.</summary>
    public string LlmModel { get; init; } = "claude-sonnet-5";

    /// <summary>How long to wait before giving up and typing the offline text.</summary>
    public int LlmTimeoutSeconds { get; init; } = 6;

    /// <summary>How much licence the cleanup pass has to change your wording.</summary>
    /// <remarks>
    /// Claude tier only — the offline rules are not tunable. Faithful by default, because a
    /// dictation tool that quietly rephrases you is a worse default than one that leaves an
    /// awkward sentence awkward.
    /// </remarks>
    public Formatting.WritingStyle WritingStyle { get; init; } = Formatting.WritingStyle.Faithful;

    /// <summary>
    /// One extra instruction of your own, appended to whichever style is selected.
    /// </summary>
    /// <remarks>
    /// For the things a preset cannot know: "British spelling", "never use em dashes", "I
    /// write commit messages, keep them imperative". Kept short deliberately — this rides on
    /// every request, and a paragraph here costs tokens on every utterance.
    /// </remarks>
    public string? StyleInstruction { get; init; }

    /// <summary>Copy the transcript to the clipboard in addition to typing it.</summary>
    public bool AlsoCopyToClipboard { get; init; }

    /// <summary>
    /// Ignore holds shorter than this. Guards against a tap of the key firing a pointless
    /// record-and-transcribe cycle.
    /// </summary>
    public int MinimumHoldMilliseconds { get; init; } = 200;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Teezy", "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static TeezySettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new TeezySettings();

            var loaded = JsonSerializer.Deserialize<TeezySettings>(File.ReadAllText(path), Json)
                ?? new TeezySettings();

            return Migrate(loaded);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file must not stop the app from starting.
            // Defaults are always a usable configuration.
            return new TeezySettings();
        }
    }

    /// <summary>Converts a settings file written before hotkeys became combinations.</summary>
    internal static TeezySettings Migrate(TeezySettings loaded)
    {
        if (loaded.LegacyPushToTalkKey is not { Length: > 0 } legacy)
        {
            // A file with neither form - hand-edited, or truncated - still needs a usable key.
            return loaded.Hotkey.IsEmpty ? loaded with { Hotkey = Hotkey.Default } : loaded;
        }

        var migrated = legacy switch
        {
            "RightControl" => new Hotkey(HotkeyKey.RightControl),
            "RightShift" => new Hotkey(HotkeyKey.RightShift),
            "ScrollLock" => new Hotkey(HotkeyKey.ScrollLock),
            "Pause" => new Hotkey(HotkeyKey.Pause),
            "F13" => new Hotkey(HotkeyKey.F13),
            _ => Hotkey.Default,
        };

        // Dropping the legacy field is what makes the migration stick: it is not written back,
        // so the next save leaves a file in the new shape only.
        return loaded with { Hotkey = migrated, LegacyPushToTalkKey = null };
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
