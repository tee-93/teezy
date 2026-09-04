using System.Runtime.InteropServices;
using Teezy.Core.Abstractions;
using Teezy.Core.Hotkeys;
using static Teezy.Platform.Windows.Native;

namespace Teezy.Platform.Windows;

/// <summary>Global push-to-talk watcher built on <c>WH_KEYBOARD_LL</c>.</summary>
/// <remarks>
/// <para>
/// <b>The hook observes and never swallows.</b> Every callback ends in
/// <see cref="CallNextHookEx"/>. Suppression would buy nothing — the supported keys produce
/// no character — and risks a much worse failure: if a key-down is consumed but the key-up
/// escapes, because the hook timed out mid-gesture or focus crossed into an elevated window,
/// the foreground app believes the modifier is held down forever.
/// </para>
/// <para>
/// <b>Must be installed from a thread with a running message pump.</b> Windows delivers
/// low-level hook callbacks by posting to the installing thread's queue; install it from a
/// pool thread and the callback simply never fires. In this app that means the UI thread.
/// </para>
/// <para>
/// <b>The delegate is held in a field on purpose.</b> Passing a lambda directly lets the GC
/// collect it while Windows still holds the pointer — which presents as the hotkey working
/// for a few minutes and then dying.
/// </para>
/// <para>
/// All the combination logic lives in <see cref="HotkeyMatcher"/>, in the testable project.
/// This class only translates Win32 key events into <see cref="HotkeyKey"/> values.
/// </para>
/// </remarks>
public sealed class WindowsHotkeySource : IHotkeySource, IHotkeyCapture
{
    private readonly LowLevelKeyboardProc _callback;   // see remarks: do not inline
    private readonly HotkeyMatcher _matcher;

    private nint _hook;

    /// <summary>Keys held so far during a capture, for the "press your combination" picker.</summary>
    private readonly HashSet<HotkeyKey> _captured = [];
    private readonly HashSet<HotkeyKey> _captureHeld = [];
    private Action<Hotkey>? _onCaptured;

    public event Action? Pressed;
    public event Action? Released;

    public WindowsHotkeySource()
    {
        _callback = HookProc;
        _matcher = new HotkeyMatcher(isPhysicallyDown: IsDown);
    }

    /// <summary>
    /// Asks the keyboard itself whether a key is down, rather than trusting our event history.
    /// </summary>
    /// <remarks>
    /// <see cref="GetAsyncKeyState"/> reports real hardware state, so it stays correct across
    /// the gaps a low-level hook has: it receives nothing while an elevated window has focus,
    /// and Windows silently evicts a hook whose callback overruns. Without this a single lost
    /// key-up left a slot satisfied forever — reported in real use as "Ctrl + Win" beginning
    /// dictation on Ctrl alone.
    /// <para>
    /// The high bit is the one that means "currently down". The low bit means "pressed since
    /// last asked" and would report a key that has already been released.
    /// </para>
    /// </remarks>
    private static bool IsDown(HotkeyKey key) =>
        VirtualKey(key) is { } vk && (GetAsyncKeyState((int)vk) & 0x8000) != 0;

    /// <summary>The virtual-key code to ask about, or null for a key with no single code.</summary>
    private static uint? VirtualKey(HotkeyKey key) => key switch
    {
        HotkeyKey.LeftControl => VK_LCONTROL,
        HotkeyKey.RightControl => VK_RCONTROL,
        HotkeyKey.LeftAlt => VK_LMENU,
        HotkeyKey.RightAlt => VK_RMENU,
        HotkeyKey.LeftShift => VK_LSHIFT,
        HotkeyKey.RightShift => VK_RSHIFT,
        HotkeyKey.LeftWindows => VK_LWIN,
        HotkeyKey.RightWindows => VK_RWIN,
        HotkeyKey.CapsLock => VK_CAPITAL,
        HotkeyKey.ScrollLock => VK_SCROLL,
        HotkeyKey.Pause => VK_PAUSE,
        >= HotkeyKey.F13 and <= HotkeyKey.F20 => VK_F13 + (uint)(key - HotkeyKey.F13),

        // Side-agnostic slots are never stored here: the matcher only ever holds the specific
        // keys the hook reported, so there is nothing to ask about.
        _ => null,
    };

    public Hotkey Hotkey
    {
        get => _matcher.Hotkey;
        set => _matcher.Hotkey = value;
    }

    public bool Start()
    {
        if (_hook != 0) return true;

        // GetModuleHandle(null) is correct for WH_KEYBOARD_LL: the hook runs in this process,
        // so it wants this module's handle rather than an injected DLL's.
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _callback, GetModuleHandleW(null), 0);

        // Key-up events that happened while the hook was down are gone. Without this the
        // matcher could believe a key is still held and never fire again.
        _matcher.Reset();

        return _hook != 0;
    }

    public void Stop()
    {
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook);
        _hook = 0;
        _matcher.Reset();
    }

    /// <summary>
    /// Captures the next combination the user holds, for the settings picker.
    /// </summary>
    /// <remarks>
    /// Reported on release of the last key rather than on the first press, so the user can
    /// build up a chord at their own pace. The largest set held at once is what counts —
    /// people rarely press every key of a combination on the same millisecond, and taking the
    /// final state would capture whatever happened to still be down.
    /// </remarks>
    public void BeginCapture(Action<Hotkey> onCaptured)
    {
        _captured.Clear();
        _captureHeld.Clear();
        _onCaptured = onCaptured;
    }

    public void CancelCapture()
    {
        _onCaptured = null;
        _captured.Clear();
        _captureHeld.Clear();
    }

    public bool IsCapturing => _onCaptured is not null;

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode != HC_ACTION) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var msg = (int)wParam;
        var isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        var isUp = msg is WM_KEYUP or WM_SYSKEYUP;

        if ((isDown || isUp) && Translate(data) is { } key)
        {
            if (_onCaptured is not null) Capture(key, isDown);
            else Dispatch(key, isDown);
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void Dispatch(HotkeyKey key, bool isDown)
    {
        switch (_matcher.Update(key, isDown))
        {
            case HotkeyTransition.Pressed: Pressed?.Invoke(); break;
            case HotkeyTransition.Released: Released?.Invoke(); break;
        }
    }

    private void Capture(HotkeyKey key, bool isDown)
    {
        // Recorded side-agnostically: someone pressing left Ctrl almost never means "left
        // Ctrl only", and a picker that silently bound one side would feel broken.
        var general = HotkeyKeys.Generalise(key);

        if (isDown)
        {
            _captureHeld.Add(general);
            _captured.UnionWith(_captureHeld);
            return;
        }

        _captureHeld.Remove(general);
        if (_captureHeld.Count > 0 || _captured.Count == 0) return;

        var result = new Hotkey(_captured);
        var callback = _onCaptured;
        CancelCapture();
        callback?.Invoke(result);
    }

    /// <summary>Maps a Win32 key event onto a <see cref="HotkeyKey"/>, or null if unsupported.</summary>
    /// <remarks>
    /// Left and right modifiers are separated by the extended-key flag, not by the virtual
    /// key code alone: a low-level hook may report either the generic <c>VK_CONTROL</c> or the
    /// specific <c>VK_RCONTROL</c> depending on the keyboard driver. Right Shift is the
    /// exception — it is <i>not</i> an extended key, and is identified by its scan code.
    /// </remarks>
    private static HotkeyKey? Translate(in KBDLLHOOKSTRUCT data)
    {
        var extended = (data.flags & LLKHF_EXTENDED) != 0;

        return data.vkCode switch
        {
            VK_LCONTROL => HotkeyKey.LeftControl,
            VK_RCONTROL => HotkeyKey.RightControl,
            VK_CONTROL => extended ? HotkeyKey.RightControl : HotkeyKey.LeftControl,

            VK_LMENU => HotkeyKey.LeftAlt,
            VK_RMENU => HotkeyKey.RightAlt,
            VK_MENU => extended ? HotkeyKey.RightAlt : HotkeyKey.LeftAlt,

            VK_LSHIFT => HotkeyKey.LeftShift,
            VK_RSHIFT => HotkeyKey.RightShift,
            VK_SHIFT => data.scanCode == RightShiftScanCode ? HotkeyKey.RightShift : HotkeyKey.LeftShift,

            VK_LWIN => HotkeyKey.LeftWindows,
            VK_RWIN => HotkeyKey.RightWindows,

            VK_CAPITAL => HotkeyKey.CapsLock,
            VK_SCROLL => HotkeyKey.ScrollLock,
            VK_PAUSE => HotkeyKey.Pause,

            >= VK_F13 and <= VK_F20 => HotkeyKey.F13 + (int)(data.vkCode - VK_F13),

            _ => null,
        };
    }

    public void Dispose() => Stop();
}
