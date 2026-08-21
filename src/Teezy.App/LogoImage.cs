using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Teezy.App;

/// <summary>The Teezy mark as a WPF image, for window and taskbar icons.</summary>
/// <remarks>
/// Rendered from the same geometry the tray icon and the in-app logo use, rather than
/// shipping an .ico. One definition means the mark cannot drift between the three places it
/// appears, and it can be produced at whatever size the shell asks for.
/// </remarks>
internal static class LogoImage
{
    public static ImageSource Create(int size)
    {
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            var scale = size / 100.0;
            var squircle = new RectangleGeometry(
                new Rect(3 * scale, 3 * scale, 94 * scale, 94 * scale),
                28 * scale, 28 * scale);

            dc.DrawGeometry(new SolidColorBrush(Brand.Primary), null, squircle);

            var mark = Geometry.Parse(Brand.MarkGeometry).Clone();
            mark.Transform = new ScaleTransform(scale, scale);
            dc.DrawGeometry(Brushes.White, null, mark);
        }

        // 96 dpi: the caller asks in pixels, so rendering at anything else would silently
        // produce an image of a different size than requested.
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
