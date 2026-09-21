using System.Windows;
using System.Windows.Media;

namespace Teezy.App;

/// <summary>The theme's colours, for the parts drawn in code rather than declared in XAML.</summary>
/// <remarks>
/// This used to be a second copy of the palette, and it drifted: it was still the old light
/// theme months after the window went dark, so anything drawn in code came out in the wrong
/// colours. Now it reads <c>Theme.xaml</c> itself, so there is exactly one palette.
/// </remarks>
internal static class Brand
{
    /// <summary>A brush from the theme, by its key.</summary>
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);

    public static Brush Accent => Brush("Accent");
    public static Brush Muted => Brush("Muted");
    public static Brush Faint => Brush("Faint");
    public static Brush Ink => Brush("Ink");
    public static Brush Record => Brush("Record");
}
