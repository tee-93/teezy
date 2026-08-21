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
    /// <summary>The blue the sound waves are drawn in.</summary>
    public static readonly Color Accent = Color.FromRgb(0x01, 0x4A, 0xFD);

    public static readonly Color AccentDeep = Color.FromRgb(0x00, 0x33, 0xC9);
    public static readonly Color AccentSoft = Color.FromRgb(0xE7, 0xED, 0xFF);

    /// <summary>The navy of the speech bubble — the mark's structural colour.</summary>
    public static readonly Color AccentInk = Color.FromRgb(0x04, 0x19, 0x45);

    /// <summary>The sparkle in the mark, and nothing else. See Theme.xaml.</summary>
    public static readonly Color MarkSparkle = Color.FromRgb(0xFC, 0xB2, 0x47);

    public static readonly Color Paper = Color.FromRgb(0xFC, 0xFA, 0xF7);
    public static readonly Color Ink = Color.FromRgb(0x1B, 0x1A, 0x18);
    public static readonly Color Muted = Color.FromRgb(0x7A, 0x75, 0x6E);
    public static readonly Color Faint = Color.FromRgb(0xA8, 0xA2, 0x9A);
    public static readonly Color Hairline = Color.FromRgb(0xE9, 0xE4, 0xDC);

    /// <summary>Recording. Nothing else in the app is this colour.</summary>
    public static readonly Color Record = Color.FromRgb(0xE5, 0x48, 0x4D);

    // The mark itself is not here. It lives in Theme.xaml as MarkImage (the full logo) and
    // MarkGeometry (the small glyph), and TrayIcons reads it from there at runtime. Keeping a
    // second copy in C# is what let the tray drift from the window before.
}
