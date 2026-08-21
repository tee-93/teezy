using System.Windows.Media;

namespace Teezy.App;

/// <summary>The palette in code, for the parts drawn rather than declared.</summary>
/// <remarks>
/// <para>
/// These must stay in step with <c>Theme.xaml</c>, which is the source of truth for anything
/// XAML can express. Only the tray icon, the window icon and the chart shading are drawn in
/// code — everything else takes its colours from the resource dictionary.
/// </para>
/// <para>
/// <b>Red is reserved.</b> It means recording and nothing else, which is why the accent is a
/// blue: a warm accent would compete with the one signal the user must read instantly.
/// </para>
/// </remarks>
internal static class Brand
{
    public static readonly Color Accent = Color.FromRgb(0x1E, 0x5F, 0x8E);
    public static readonly Color AccentDeep = Color.FromRgb(0x16, 0x48, 0x6D);
    public static readonly Color AccentSoft = Color.FromRgb(0xE4, 0xEF, 0xF7);

    public static readonly Color Paper = Color.FromRgb(0xFC, 0xFA, 0xF7);
    public static readonly Color Ink = Color.FromRgb(0x1B, 0x1A, 0x18);
    public static readonly Color Muted = Color.FromRgb(0x7A, 0x75, 0x6E);
    public static readonly Color Faint = Color.FromRgb(0xA8, 0xA2, 0x9A);
    public static readonly Color Hairline = Color.FromRgb(0xE9, 0xE4, 0xDC);

    /// <summary>Recording. Nothing else in the app is this colour.</summary>
    public static readonly Color Record = Color.FromRgb(0xE5, 0x48, 0x4D);

    /// <summary>
    /// The Teezy mark: a "T" whose stem is a rounded microphone capsule.
    /// </summary>
    /// <remarks>
    /// Authored on a 100×100 grid so one definition serves the tray icon, the window icon and
    /// the in-app logo. Two rounded bars rather than lettering — a real glyph at 16 px in the
    /// system tray turns to mush, and this still reads as a T at that size.
    /// </remarks>
    public const string MarkGeometry =
        "M22,25 L78,25 A7,7 0 0 1 78,39 L57,39 L57,63 A7,7 0 0 1 43,63 L43,39 L22,39 A7,7 0 0 1 22,25 Z";
}
