using Teezy.Core.Hotkeys;

namespace Teezy.Core.Abstractions;

/// <summary>A global, always-on watcher for the push-to-talk combinations.</summary>
/// <remarks>
/// <para>
/// The keys are <b>observed, never swallowed</b>. Suppression buys nothing here and risks a
/// far worse failure: if a key-down is consumed but the key-up escapes — a hook that timed
/// out mid-gesture, or focus crossing into an elevated window — the foreground app believes
/// the modifier is held down forever.
/// </para>
/// <para>
/// Several combinations are watched at once, one per <see cref="HotkeyAction"/>, because a
/// single hook can serve all of them. Which one is in force when they overlap is decided by
/// <see cref="HotkeyBindings"/>, in the testable project.
/// </para>
/// </remarks>
public interface IHotkeySource : IDisposable
{
    /// <summary>Fires when every key of a binding is held, naming which one.</summary>
    event Action<HotkeyAction>? Pressed;

    /// <summary>Fires when a held binding stops being fully held.</summary>
    event Action<HotkeyAction>? Released;

    /// <summary>
    /// The combinations to watch. Replacing them takes effect immediately and forgets any
    /// keys currently believed held.
    /// </summary>
    IReadOnlyDictionary<HotkeyAction, Hotkey> Bindings { get; set; }

    /// <summary>Installs the hook.</summary>
    /// <returns><c>false</c> if the hook could not be installed.</returns>
    bool Start();

    void Stop();
}
