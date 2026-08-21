using Teezy.Core.Hotkeys;

namespace Teezy.Core.Abstractions;

/// <summary>A global, always-on watcher for the push-to-talk combination.</summary>
/// <remarks>
/// The keys are <b>observed, never swallowed</b>. Suppression buys nothing here and risks a
/// far worse failure: if a key-down is consumed but the key-up escapes — a hook that timed
/// out mid-gesture, or focus crossing into an elevated window — the foreground app believes
/// the modifier is held down forever.
/// </remarks>
public interface IHotkeySource : IDisposable
{
    /// <summary>Fires once when every key in the combination is held.</summary>
    event Action? Pressed;

    /// <summary>Fires when the combination stops being fully held.</summary>
    event Action? Released;

    /// <summary>The combination to watch. Changing it takes effect immediately.</summary>
    Hotkey Hotkey { get; set; }

    /// <summary>Installs the hook.</summary>
    /// <returns><c>false</c> if the hook could not be installed.</returns>
    bool Start();

    void Stop();
}
