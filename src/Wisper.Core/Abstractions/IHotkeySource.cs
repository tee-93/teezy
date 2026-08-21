namespace Wisper.Core.Abstractions;

/// <summary>Keys offered for push-to-talk.</summary>
/// <remarks>
/// <b>Right Alt is deliberately absent.</b> It is AltGr on German, Polish, UK, Nordic and
/// most Latin-American layouts — it is how those users type <c>@</c>, <c>€</c>, <c>\</c> and
/// <c>|</c>. Binding push-to-talk there would break ordinary typing. Right Ctrl produces no
/// character on any layout, which is why it is the default.
/// </remarks>
public enum PushToTalkKey
{
    RightControl,
    RightShift,
    ScrollLock,
    Pause,
    F13,
}

/// <summary>A global, always-on watcher for the push-to-talk key.</summary>
/// <remarks>
/// The key is <b>observed, never swallowed</b>. Suppression buys nothing here and risks a
/// far worse failure: if the key-down is consumed but the key-up escapes — a hook that timed
/// out mid-gesture, or focus crossing into an elevated window — the foreground app believes
/// Ctrl is held down forever.
/// </remarks>
public interface IHotkeySource : IDisposable
{
    event Action? Pressed;
    event Action? Released;

    /// <summary>Which key to watch. Takes effect on the next <see cref="Start"/>.</summary>
    PushToTalkKey Key { get; set; }

    /// <summary>Installs the hook.</summary>
    /// <returns><c>false</c> if the hook could not be installed.</returns>
    bool Start();

    void Stop();
}
