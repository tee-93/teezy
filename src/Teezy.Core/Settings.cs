using System.Text.Json;
using System.Text.Json.Serialization;
using Teezy.Core.Abstractions;

namespace Teezy.Core;

/// <summary>User settings, persisted as JSON next to the model and dictionary.</summary>
public sealed record TeezySettings
{
    public PushToTalkKey PushToTalkKey { get; init; } = PushToTalkKey.RightControl;

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
            return File.Exists(path)
                ? JsonSerializer.Deserialize<TeezySettings>(File.ReadAllText(path), Json) ?? new()
                : new TeezySettings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable settings file must not stop the app from starting.
            // Defaults are always a usable configuration.
            return new TeezySettings();
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }
}
