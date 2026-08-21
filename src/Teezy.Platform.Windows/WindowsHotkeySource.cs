using System.Runtime.InteropServices;
using Teezy.Core.Abstractions;
using static Teezy.Platform.Windows.Native;

namespace Teezy.Platform.Windows;

/// <summary>Global push-to-talk watcher built on <c>WH_KEYBOARD_LL</c>.</summary>
/// <remarks>
/// <para>
/// <b>The hook observes and never swallows.</b> Every callback ends in
/// <see cref="CallNextHookEx"/>. Suppression would buy nothing — the supported keys produce
/// no character — and it risks a much worse failure: if a key-down is consumed but the
/// key-up escapes, because the hook timed out mid-gesture or focus crossed into an elevated
/// window, the foreground app believes the modifier is held down forever.
/// </para>
/// <para>
/// <b>Must be installed from a thread with a running message pump.</b> Windows delivers
/// low-level hook callbacks by posting to the installing thread's queue; install it from a
/// pool thread and the callback simply never fires. In this app that means the UI thread.
/// </para>
/// <para>
/// <b>The delegate is held in a field on purpose.</b> Passing a lambda directly lets the GC
/// collect it while Windows still holds the pointer — which presents as the hotkey working
/// for a few minutes and then dying, a genuinely miserable bug to chase.
/// </para>
/// </remarks>
public sealed class WindowsHotkeySource : IHotkeySource
{
    private readonly LowLevelKeyboardProc _callback;   // see remarks: do not inline
    private nint _hook;
    private bool _isDown;

    public event Action? Pressed;
    public event Action? Released;

    public PushToTalkKey Key { get; set; } = PushToTalkKey.RightControl;

    public WindowsHotkeySource() => _callback = HookProc;

    public bool Start()
    {
        if (_hook != 0) return true;

        // GetModuleHandle(null) is correct for WH_KEYBOARD_LL: the hook runs in this process,
        // so it wants this module's handle rather than an injected DLL's.
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _callback, GetModuleHandleW(null), 0);
        return _hook != 0;
    }

    public void Stop()
    {
        if (_hook == 0) return;
        UnhookWindowsHookEx(_hook);
        _hook = 0;
        _isDown = false;
    }

    private nint HookProc(int nCode, nint wParam, nint lParam)
    {
        if (nCode != HC_ACTION) return CallNextHookEx(_hook, nCode, wParam, lParam);

        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var msg = (int)wParam;

        if (Matches(data))
        {
            var down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            var up = msg is WM_KEYUP or WM_SYSKEYUP;

            // Auto-repeat resends key-down while the key is held, so edges must be tracked
            // explicitly. Without this the controller would be told "pressed" 30 times a
            // second for the whole hold.
            if (down && !_isDown)
            {
                _isDown = true;
                Pressed?.Invoke();
            }
            else if (up && _isDown)
            {
                _isDown = false;
                Released?.Invoke();
            }
        }

        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Is this event the configured key?</summary>
    /// <remarks>
    /// Left and right modifiers are separated by the extended-key flag, not by the virtual
    /// key code alone: a low-level hook may report either the generic <c>VK_CONTROL</c> or
    /// the specific <c>VK_RCONTROL</c> depending on the keyboard driver, so both are accepted
    /// and the flag is what actually decides. Treating a generic <c>VK_CONTROL</c> as a match
    /// without checking the flag would arm push-to-talk on <i>Left</i> Ctrl too, hijacking
    /// every copy and paste on the machine.
    /// </remarks>
    private bool Matches(in KBDLLHOOKSTRUCT data)
    {
        var extended = (data.flags & LLKHF_EXTENDED) != 0;

        return Key switch
        {
            PushToTalkKey.RightControl =>
                data.vkCode == VK_RCONTROL || (data.vkCode == VK_CONTROL && extended),
            PushToTalkKey.RightShift =>
                // Right Shift is the exception: it is *not* an extended key, and is
                // distinguished by its scan code (0x36) rather than by the flag.
                data.vkCode == VK_RSHIFT || (data.vkCode == VK_SHIFT && data.scanCode == 0x36),
            PushToTalkKey.ScrollLock => data.vkCode == VK_SCROLL,
            PushToTalkKey.Pause => data.vkCode == VK_PAUSE,
            PushToTalkKey.F13 => data.vkCode == VK_F13,
            _ => false,
        };
    }

    public void Dispose() => Stop();
}
