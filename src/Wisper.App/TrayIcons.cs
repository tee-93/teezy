using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Wisper.App;

/// <summary>Tray icons, drawn at runtime.</summary>
/// <remarks>
/// Drawn rather than shipped as .ico files so there is no binary asset to keep in sync with
/// the palette, and so the icon can be regenerated at the DPI the shell actually asks for.
/// </remarks>
internal static class TrayIcons
{
    private static Icon? _idle;
    private static Icon? _loading;

    /// <summary>Filled mark: armed and ready.</summary>
    public static Icon Idle => _idle ??= Draw(Color.FromArgb(0xE8, 0xE8, 0xED), filled: true);

    /// <summary>Outline only: the model is still loading, so a hold would do nothing yet.</summary>
    public static Icon Loading => _loading ??= Draw(Color.FromArgb(0x8E, 0x8E, 0x93), filled: false);

    /// <summary>A microphone capsule — readable at 16 px, which rules out anything detailed.</summary>
    private static Icon Draw(Color color, bool filled)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var pen = new Pen(color, 2.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var brush = new SolidBrush(color);

            var capsule = new Rectangle(11, 5, 10, 15);
            if (filled) g.FillPath(brush, Rounded(capsule, 5));
            else g.DrawPath(pen, Rounded(capsule, 5));

            // The cradle and stem, which are what make it read as a microphone at all.
            g.DrawArc(pen, 7, 12, 18, 15, 20, 140);
            g.DrawLine(pen, 16, 25, 16, 28);
        }

        // GetHicon hands back an unmanaged icon handle. Clone into a managed Icon and destroy
        // the original immediately, or every call leaks a GDI handle.
        var handle = bitmap.GetHicon();
        try
        {
            using var unmanaged = Icon.FromHandle(handle);
            return (Icon)unmanaged.Clone();
        }
        finally
        {
            NativeGdi.DestroyIcon(handle);
        }
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void Dispose()
    {
        _idle?.Dispose();
        _loading?.Dispose();
        _idle = _loading = null;
    }
}

internal static partial class NativeGdi
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint handle);
}
