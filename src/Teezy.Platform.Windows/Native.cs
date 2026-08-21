using System.Runtime.InteropServices;

namespace Teezy.Platform.Windows;

/// <summary>Raw Win32 surface. Declarations only — no policy lives here.</summary>
internal static partial class Native
{
    internal const int WH_KEYBOARD_LL = 13;
    internal const int HC_ACTION = 0;

    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_KEYUP = 0x0002;
    internal const uint KEYEVENTF_UNICODE = 0x0004;

    // uint to match KBDLLHOOKSTRUCT.vkCode, which is what these are compared against.
    internal const uint VK_SHIFT = 0x10;
    internal const uint VK_CONTROL = 0x11;
    internal const uint VK_MENU = 0x12;
    internal const uint VK_PAUSE = 0x13;
    internal const uint VK_CAPITAL = 0x14;
    internal const uint VK_LWIN = 0x5B;
    internal const uint VK_RWIN = 0x5C;
    internal const uint VK_F13 = 0x7C;
    internal const uint VK_F20 = 0x83;
    internal const uint VK_LSHIFT = 0xA0;
    internal const uint VK_RSHIFT = 0xA1;
    internal const uint VK_LCONTROL = 0xA2;
    internal const uint VK_RCONTROL = 0xA3;
    internal const uint VK_LMENU = 0xA4;
    internal const uint VK_RMENU = 0xA5;
    internal const uint VK_SCROLL = 0x91;

    /// <summary>Right Shift is not an extended key; its scan code is what identifies it.</summary>
    internal const uint RightShiftScanCode = 0x36;

    /// <summary>For the Ctrl+V paste fallback in the injector.</summary>
    internal const ushort VK_V = 0x56;

    /// <summary>Set on the extended-key half of a pair — the flag that separates Right Ctrl
    /// from Left Ctrl when the virtual-key code alone does not.</summary>
    internal const uint LLKHF_EXTENDED = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        // Padding so the struct matches the largest member (MOUSEINPUT) on 64-bit.
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nuint dwExtraInfo;
    }

    internal delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(nint hhk);

    [LibraryImport("user32.dll")]
    internal static partial nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint GetModuleHandleW(string? lpModuleName);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint SendInput(uint nInputs, [In] INPUT[] pInputs, int cbSize);

    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);
}
