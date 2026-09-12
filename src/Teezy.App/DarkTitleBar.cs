using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Teezy.App;

/// <summary>Asks Windows to draw this window's chrome dark.</summary>
/// <remarks>
/// <para>
/// WPF does not follow an application's own palette into the title bar — that strip is drawn by
/// the desktop window manager, and without this a dark window wears a white cap. It is the one
/// piece of the redesign that cannot be done in <c>Theme.xaml</c>.
/// </para>
/// <para>
/// The attribute number changed during Windows 10's life: 19 on builds before 18985, 20 after.
/// Both are set, and a wrong one on a given build simply returns a failure code — cheaper than
/// reading the build number and choosing, and it keeps working on both.
/// </para>
/// </remarks>
internal static class DarkTitleBar
{
    private const int UseImmersiveDarkModeBefore20H1 = 19;
    private const int UseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr window, int attribute, ref int value, int size);

    /// <summary>Applies it now if the handle exists, or as soon as it does.</summary>
    /// <remarks>
    /// A Window has no handle until it is shown, and setting the attribute before that silently
    /// does nothing — which is exactly the sort of failure that looks like the call not working.
    /// </remarks>
    public static void Apply(Window window)
    {
        if (new WindowInteropHelper(window).Handle is { } handle && handle != IntPtr.Zero)
        {
            Set(handle);
            return;
        }

        window.SourceInitialized += OnReady;

        void OnReady(object? sender, EventArgs e)
        {
            window.SourceInitialized -= OnReady;
            Set(new WindowInteropHelper(window).Handle);
        }
    }

    private static void Set(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        var on = 1;

        try
        {
            // The result is deliberately discarded: on any given build one of these two IS the
            // wrong attribute and returns a failure, which is the whole point of sending both.
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref on, sizeof(int));
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkModeBefore20H1, ref on, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // Nothing to do and nothing worth saying: a light title bar is cosmetic, and an app
            // that refused to open over it would be far worse than one wearing the wrong cap.
        }
    }
}
