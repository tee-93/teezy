using System.Text.Json.Serialization;

namespace Teezy.Core.Hotkeys;

/// <summary>A push-to-talk combination: every key must be held together.</summary>
public sealed record Hotkey
{
    /// <summary>More than this and it stops being something you can hold while talking.</summary>
    public const int MaxKeys = 4;

    public IReadOnlyList<HotkeyKey> Keys { get; init; } = [];

    public Hotkey() { }

    public Hotkey(params HotkeyKey[] keys) => Keys = Normalise(keys);

    public Hotkey(IEnumerable<HotkeyKey> keys) => Keys = Normalise(keys);

    /// <remarks>
    /// Computed, so it is not written to settings.json. Without this the file carried three
    /// derived copies of the combination — <c>IsEmpty</c>, <c>Display</c> and
    /// <c>Warnings</c> — that nothing ever read back, so a hand-edited file could disagree
    /// with itself and the copy the app actually obeyed was the least prominent one in it.
    /// </remarks>
    [JsonIgnore]
    public bool IsEmpty => Keys.Count == 0;

    /// <summary>
    /// Ctrl + Win — the default.
    /// </summary>
    /// <remarks>
    /// A chord rather than a lone key, and specifically one that produces no character and
    /// fires no system action. Win <i>alone</i> would open the Start menu on release; holding
    /// Ctrl with it makes Windows treat the pair as a chord and suppresses that.
    /// </remarks>
    public static Hotkey Default { get; } = new(HotkeyKey.Control, HotkeyKey.Windows);

    /// <summary>Deduplicated and ordered, so display and comparison are stable.</summary>
    private static IReadOnlyList<HotkeyKey> Normalise(IEnumerable<HotkeyKey> keys) =>
        [.. keys.Distinct()
            .OrderBy(HotkeyKeys.DisplayOrder)
            .ThenBy(k => k.ToString(), StringComparer.Ordinal)
            .Take(MaxKeys)];

    [JsonIgnore]
    public string Display => IsEmpty
        ? "Not set"
        : string.Join(" + ", Keys.Select(HotkeyKeys.Label));

    /// <summary>Records are reference-compared on their list property, so compare contents.</summary>
    public bool Equals(Hotkey? other) =>
        other is not null && Keys.SequenceEqual(other.Keys);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var key in Keys) hash.Add(key);
        return hash.ToHashCode();
    }

    /// <summary>Reasons this combination will annoy the user, in their own terms.</summary>
    /// <remarks>
    /// Warnings rather than restrictions. Every item here is a real, reproducible nuisance,
    /// but it is the user's keyboard — someone who has already turned off Filter Keys should
    /// not be stopped from using Shift.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<string> Warnings
    {
        get
        {
            var warnings = new List<string>();

            if (IsEmpty)
            {
                warnings.Add("No keys set — dictation cannot be triggered.");
                return warnings;
            }

            // Holding either Shift for eight seconds opens the Filter Keys prompt, and a
            // push-to-talk hold routinely runs longer than that.
            if (Keys.Any(k => HotkeyKeys.Generalise(k) == HotkeyKey.Shift))
            {
                warnings.Add(
                    "Holding Shift for eight seconds triggers the Windows Filter Keys prompt. "
                    + "Ctrl or Win avoid it.");
            }

            // A lone Win key opens the Start menu when released. Paired with another modifier
            // Windows treats it as a chord and does not.
            if (Keys.Count == 1 && HotkeyKeys.Generalise(Keys[0]) == HotkeyKey.Windows)
            {
                warnings.Add(
                    "The Windows key on its own opens the Start menu when released. "
                    + "Pair it with Ctrl or Alt.");
            }

            if (Keys.Count == 1 && HotkeyKeys.Generalise(Keys[0]) == HotkeyKey.Alt)
            {
                warnings.Add(
                    "Alt on its own activates the menu bar in many apps, and Right Alt is "
                    + "AltGr on many keyboard layouts.");
            }

            if (Keys.Any(k => k is HotkeyKey.RightAlt))
            {
                warnings.Add(
                    "Right Alt is AltGr on German, Polish, UK, Nordic and most Latin-American "
                    + "layouts — it is how those keyboards type @, €, \\ and |.");
            }

            // Side-agnostic Ctrl alone fires on every copy and paste.
            if (Keys is [HotkeyKey.Control])
            {
                warnings.Add("Plain Ctrl fires on every Ctrl+C and Ctrl+V. Add a second key.");
            }

            if (Keys.Any(k => k == HotkeyKey.CapsLock))
            {
                warnings.Add("Caps Lock toggles on each press, so it will also change your typing case.");
            }

            return warnings;
        }
    }
}
