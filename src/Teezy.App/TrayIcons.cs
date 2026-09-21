using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Teezy.App;

/// <summary>Tray icons, drawn at runtime from the mark in <c>Theme.xaml</c>.</summary>
/// <remarks>
/// <para>
/// Drawn rather than shipped as .ico files so there is no binary asset to keep in step with
/// the palette, and so state can change the icon without a second file.
/// </para>
/// <para>
/// The geometry is <b>read from the resource dictionary, not restated here</b>. This used to
/// be hand-drawn GDI+ rounded rectangles that happened to match the XAML; they matched only
/// for as long as someone remembered to change both. Rendering the real resource means the
/// tray cannot disagree with the window, whatever the mark becomes next.
/// </para>
/// <para>
/// The mark stands on its own, with no tile behind it — the same rule as Fivebar's. What
/// changes with state is only its colour.
/// </para>
/// </remarks>
internal static class TrayIcons
{
    private static System.Drawing.Icon? _ready;
    private static System.Drawing.Icon? _busy;
    private static System.Drawing.Icon? _recording;

    /// <summary>
    /// Rendered at 32 px and handed to the shell, which scales down to whatever the tray is
    /// using. Rendering at 16 px instead would look sharper in the tray and wrong everywhere
    /// else the icon appears.
    /// </summary>
    private const int Size = 32;

    /// <summary>Armed and ready: the mark in its own colour.</summary>
    public static System.Drawing.Icon Ready => _ready ??= Draw(Resource<Color>("MarkColor"));

    /// <summary>Model loading or not installed — a hold would do nothing yet.</summary>
    public static System.Drawing.Icon Busy => _busy ??= Draw(Resource<SolidColorBrush>("Muted").Color);

    /// <summary>Microphone open.</summary>
    public static System.Drawing.Icon Recording => _recording ??= Draw(Resource<SolidColorBrush>("Record").Color);

    private static T Resource<T>(string key) => (T)Application.Current.FindResource(key);

    private static System.Drawing.Icon Draw(Color colour)
    {
        var glyph = Resource<Geometry>("MarkGeometry");
        var fill = Resource<double>("MarkGlyphFill");

        // Everything in the resource dictionary is authored on a 100x100 grid.
        const double s = Size / 100.0;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Centred from the geometry's own bounds rather than from remembered numbers, so
            // re-drawing the glyph re-centres it instead of quietly sitting off to one side.
            var bounds = glyph.Bounds;
            var k = s * fill;
            var placed = glyph.Clone();
            placed.Transform = new TransformGroup
            {
                Children =
                {
                    new ScaleTransform(k, k),
                    new TranslateTransform(
                        (Size - bounds.Width * k) / 2 - bounds.X * k,
                        (Size - bounds.Height * k) / 2 - bounds.Y * k),
                },
            };
            dc.DrawGeometry(new SolidColorBrush(colour), null, placed);
        }

        var rendered = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);

        // WPF and GDI+ share no bitmap type, and a PNG round-trip is the one conversion that
        // carries the alpha channel intact — a mask built from a flattened bitmap would give
        // the icon hard, aliased edges.
        using var stream = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        encoder.Save(stream);
        stream.Position = 0;

        using var bitmap = new System.Drawing.Bitmap(stream);

        // GetHicon returns an unmanaged handle. Clone into a managed Icon and destroy the
        // original at once, or every call leaks a GDI handle.
        var handle = bitmap.GetHicon();
        try
        {
            using var unmanaged = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)unmanaged.Clone();
        }
        finally
        {
            NativeGdi.DestroyIcon(handle);
        }
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
