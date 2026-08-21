using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Teezy.App;

/// <summary>Tray icons, drawn at runtime.</summary>
/// <remarks>
/// Drawn rather than shipped as .ico files so there is no binary asset to keep in step with
/// the palette, and so state can change the icon without a second file. The mark is two
/// rounded bars, not lettering: a real glyph at 16 px turns to mush.
/// </remarks>
internal static class TrayIcons
{
    private static Icon? _ready;
    private static Icon? _busy;
    private static Icon? _recording;

    /// <summary>
    /// GDI+ and WPF have separate <c>Color</c> types, which is the only reason these were
    /// ever written out twice. Converting instead means the tray cannot drift from
    /// <see cref="Brand"/> the next time the palette moves.
    /// </summary>
    private static Color Gdi(System.Windows.Media.Color c) => Color.FromArgb(c.R, c.G, c.B);

    private static readonly Color Accent = Gdi(Brand.Accent);
    private static readonly Color Muted = Gdi(Brand.Muted);
    private static readonly Color Record = Gdi(Brand.Record);

    /// <summary>Armed and ready.</summary>
    public static Icon Ready => _ready ??= Draw(Accent);

    /// <summary>Model loading or not installed — a hold would do nothing yet.</summary>
    public static Icon Busy => _busy ??= Draw(Muted);

    /// <summary>Microphone open.</summary>
    public static Icon Recording => _recording ??= Draw(Record);

    private static Icon Draw(Color background)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var back = new SolidBrush(background);
            using var mark = new SolidBrush(Color.White);

            using (var squircle = Rounded(new RectangleF(1, 1, 30, 30), 9.5f))
            {
                g.FillPath(back, squircle);
            }

            // Same 100x100 grid as Brand.MarkGeometry, scaled to 32 px.
            const float s = 32f / 100f;
            using (var bar = Rounded(new RectangleF(22 * s, 25 * s, 56 * s, 14 * s), 7 * s))
            {
                g.FillPath(mark, bar);
            }
            using (var stem = Rounded(new RectangleF(43 * s, 25 * s, 14 * s, 45 * s), 7 * s))
            {
                g.FillPath(mark, stem);
            }
        }

        // GetHicon returns an unmanaged handle. Clone into a managed Icon and destroy the
        // original at once, or every call leaks a GDI handle.
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

    private static GraphicsPath Rounded(RectangleF r, float radius)
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
        _ready?.Dispose();
        _busy?.Dispose();
        _recording?.Dispose();
        _ready = _busy = _recording = null;
    }
}

internal static partial class NativeGdi
{
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint handle);
}
