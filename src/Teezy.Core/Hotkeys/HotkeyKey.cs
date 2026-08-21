namespace Teezy.Core.Hotkeys;

/// <summary>A key that can take part in a push-to-talk combination.</summary>
/// <remarks>
/// Side-agnostic and side-specific entries both exist on purpose. <see cref="Control"/>
/// accepts either Ctrl, which is what people mean when they say "Ctrl+Win"; the
/// <see cref="LeftControl"/> / <see cref="RightControl"/> pair is for anyone who wants one
/// hand's key left free.
/// </remarks>
public enum HotkeyKey
{
    Control, LeftControl, RightControl,
    Alt, LeftAlt, RightAlt,
    Shift, LeftShift, RightShift,
    Windows, LeftWindows, RightWindows,
    CapsLock, ScrollLock, Pause,
    F13, F14, F15, F16, F17, F18, F19, F20,
}

public static class HotkeyKeys
{
    /// <summary>Does an actual key press satisfy a slot in a combination?</summary>
    /// <remarks>
    /// The hook always reports a specific physical key. A combination asking for
    /// <see cref="HotkeyKey.Control"/> must therefore accept either side.
    /// </remarks>
    public static bool Satisfies(HotkeyKey slot, HotkeyKey actual) => slot switch
    {
        HotkeyKey.Control => actual is HotkeyKey.LeftControl or HotkeyKey.RightControl,
        HotkeyKey.Alt => actual is HotkeyKey.LeftAlt or HotkeyKey.RightAlt,
        HotkeyKey.Shift => actual is HotkeyKey.LeftShift or HotkeyKey.RightShift,
        HotkeyKey.Windows => actual is HotkeyKey.LeftWindows or HotkeyKey.RightWindows,
        _ => slot == actual,
    };

    /// <summary>True for keys that only modify other keys.</summary>
    public static bool IsModifier(HotkeyKey key) => key
        is HotkeyKey.Control or HotkeyKey.LeftControl or HotkeyKey.RightControl
        or HotkeyKey.Alt or HotkeyKey.LeftAlt or HotkeyKey.RightAlt
        or HotkeyKey.Shift or HotkeyKey.LeftShift or HotkeyKey.RightShift
        or HotkeyKey.Windows or HotkeyKey.LeftWindows or HotkeyKey.RightWindows;

    /// <summary>Collapses a specific key to its side-agnostic form, for display and capture.</summary>
    public static HotkeyKey Generalise(HotkeyKey key) => key switch
    {
        HotkeyKey.LeftControl or HotkeyKey.RightControl => HotkeyKey.Control,
        HotkeyKey.LeftAlt or HotkeyKey.RightAlt => HotkeyKey.Alt,
        HotkeyKey.LeftShift or HotkeyKey.RightShift => HotkeyKey.Shift,
        HotkeyKey.LeftWindows or HotkeyKey.RightWindows => HotkeyKey.Windows,
        _ => key,
    };

    public static string Label(HotkeyKey key) => key switch
    {
        HotkeyKey.Control => "Ctrl",
        HotkeyKey.LeftControl => "Left Ctrl",
        HotkeyKey.RightControl => "Right Ctrl",
        HotkeyKey.Alt => "Alt",
        HotkeyKey.LeftAlt => "Left Alt",
        HotkeyKey.RightAlt => "Right Alt",
        HotkeyKey.Shift => "Shift",
        HotkeyKey.LeftShift => "Left Shift",
        HotkeyKey.RightShift => "Right Shift",
        HotkeyKey.Windows => "Win",
        HotkeyKey.LeftWindows => "Left Win",
        HotkeyKey.RightWindows => "Right Win",
        HotkeyKey.CapsLock => "Caps Lock",
        HotkeyKey.ScrollLock => "Scroll Lock",
        HotkeyKey.Pause => "Pause",
        _ => key.ToString(),
    };

    /// <summary>
    /// Sort order for display, so a combination always reads the same way round.
    /// </summary>
    /// <remarks>
    /// "Ctrl + Win", never "Win + Ctrl". Users read a hotkey as a fixed phrase and a
    /// combination that renders differently depending on press order looks like a bug.
    /// </remarks>
    public static int DisplayOrder(HotkeyKey key) => key switch
    {
        HotkeyKey.Control or HotkeyKey.LeftControl or HotkeyKey.RightControl => 0,
        HotkeyKey.Alt or HotkeyKey.LeftAlt or HotkeyKey.RightAlt => 1,
        HotkeyKey.Shift or HotkeyKey.LeftShift or HotkeyKey.RightShift => 2,
        HotkeyKey.Windows or HotkeyKey.LeftWindows or HotkeyKey.RightWindows => 3,
        _ => 4,
    };
}
