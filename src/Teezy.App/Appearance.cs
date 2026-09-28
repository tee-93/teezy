using System;
using System.Windows;
using Teezy.Core;
using Teezy.Platform.Windows;

namespace Teezy.App;

/// <summary>Which palette is on screen, and the one-time work of putting it there.</summary>
/// <remarks>
/// <para>
/// Chosen once, at startup, from Settings ▸ Appearance: light, dark, or match Windows. Changing
/// it takes effect on the next launch. <c>Theme.xaml</c>'s styles are parsed once, against
/// whichever palette dictionary is merged ahead of it — swapping that dictionary while windows
/// are already open would not repaint them, because a <c>StaticResource</c> lookup is resolved
/// once, when the XAML that used it is loaded, not looked up again afterwards. A live switch
/// would need every view converted to <c>DynamicResource</c> first; this does not attempt that.
/// </para>
/// </remarks>
internal static class Appearance
{
    /// <summary>
    /// Whether the resolved theme is dark, for the one thing that cannot read it from a
    /// resource: the window title bar, which Windows itself draws.
    /// </summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Merges the right palette, then <c>Theme.xaml</c> itself, into the app's resources.</summary>
    /// <remarks>Must run before any window is constructed — its XAML is parsed against whatever is merged so far.</remarks>
    public static void Apply(Application app, AppTheme theme)
    {
        IsDark = theme switch
        {
            AppTheme.Light => false,
            AppTheme.Dark => true,
            _ => !WindowsTheme.AppsUseLightTheme(),
        };

        app.Resources.MergedDictionaries.Add(Load(IsDark ? "Theme.Dark.xaml" : "Theme.Light.xaml"));
        app.Resources.MergedDictionaries.Add(Load("Theme.xaml"));
    }

    private static ResourceDictionary Load(string file) =>
        new() { Source = new Uri($"pack://application:,,,/Teezy;component/{file}") };
}
