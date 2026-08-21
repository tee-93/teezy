using System.Windows.Media;

namespace Teezy.App;

/// <summary>Every colour and shape the app is allowed to use.</summary>
/// <remarks>
/// <para>
/// Views must not contain literal colours. If something needs a value that is not here, add
/// it here rather than inlining it — that is what keeps the HUD, the tray icon and the main
/// window looking like one product.
/// </para>
/// <para>
/// <b>Red is reserved.</b> It means recording and nothing else, which is why the brand colour
/// is indigo: an orange or crimson accent would compete with the one signal the user must be
/// able to read instantly.
/// </para>
/// </remarks>
internal static class Brand
{
    // Identity
    public static readonly Color Primary = Color.FromRgb(0x5B, 0x4C, 0xE0);   // indigo
    public static readonly Color PrimaryDeep = Color.FromRgb(0x3D, 0x30, 0xB0);
    public static readonly Color PrimarySoft = Color.FromRgb(0xEE, 0xEC, 0xFD);

    // Surfaces
    public static readonly Color Ink = Color.FromRgb(0x17, 0x17, 0x1F);
    public static readonly Color Surface = Color.FromRgb(0xFB, 0xFB, 0xFD);
    public static readonly Color Card = Colors.White;
    public static readonly Color Hairline = Color.FromRgb(0xE6, 0xE6, 0xEC);
    public static readonly Color Muted = Color.FromRgb(0x6E, 0x6E, 0x7A);
    public static readonly Color Faint = Color.FromRgb(0x9A, 0x9A, 0xA6);

    /// <summary>Recording. Nothing else in the app is this colour.</summary>
    public static readonly Color Record = Color.FromRgb(0xFF, 0x3B, 0x30);

    // Instrumentation only — meters and charts, never UI chrome.
    public static readonly Color MeterHigh = Color.FromRgb(0x5B, 0x4C, 0xE0);
    public static readonly Color MeterLow = Color.FromRgb(0xC7, 0xC2, 0xF5);

    public static SolidColorBrush BrushOf(Color c) => new(c);

    /// <summary>
    /// The Teezy mark: a "T" whose stem widens into a voice bar.
    /// </summary>
    /// <remarks>
    /// Authored on a 100×100 grid so one definition serves every size. Deliberately built
    /// from two rounded bars rather than lettering — at 16 px in the system tray, a real
    /// glyph turns to mush, while two rectangles stay readable.
    /// </remarks>
    public const string MarkGeometry =
        "M24,26 L76,26 A6,6 0 0 1 76,40 L58,40 L58,72 " +
        "A8,8 0 0 1 42,72 L42,40 L24,40 A6,6 0 0 1 24,26 Z";
}
