using System;
using System.Runtime.InteropServices;

namespace Teezy.App;

/// <summary>Window-style interop for the HUD.</summary>
internal static partial class Native
{
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPtrW(nint hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPtrW(nint hWnd, int nIndex, nint dwNewLong);
}
