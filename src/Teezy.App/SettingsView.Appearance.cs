using System;
using System.Windows;
using System.Windows.Controls;
using Teezy.Core;

namespace Teezy.App;

/// <summary>Settings ▸ Appearance: light, dark, or match Windows.</summary>
/// <remarks>
/// The choice is saved immediately, like every other setting on this page, but does not repaint
/// anything — <see cref="Appearance"/> picks the palette once, at startup. So this page is also
/// where the gap is owned up to: a notice offers to restart TeezyFlow the moment the choice
/// changes to something that is not already on screen.
/// </remarks>
public partial class SettingsView
{
    private void ShowAppearanceSettings(TeezySettings settings)
    {
        (settings.Theme switch
        {
            AppTheme.Light => ThemeLight,
            AppTheme.System => ThemeSystem,
            _ => ThemeDark,
        }).IsChecked = true;

        ThemeHint.Text = settings.Theme switch
        {
            AppTheme.Light => "Bright, for daylight.",
            AppTheme.System => "Follows Windows' own light-or-dark setting, checked each time TeezyFlow starts.",
            _ => "TeezyFlow's own look since the beginning — easy on the eyes.",
        };

        RestartNotice.Visibility = Visibility.Collapsed;
    }

    private void OnThemeChosen(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { IsChecked: true } chosen) return;

        var theme = chosen.Name switch
        {
            nameof(ThemeLight) => AppTheme.Light,
            nameof(ThemeSystem) => AppTheme.System,
            _ => AppTheme.Dark,
        };

        var settings = _read();
        if (theme == settings.Theme) return;

        _write(settings with { Theme = theme });
        ShowAppearanceSettings(settings with { Theme = theme });

        // The one thing worth telling them mid-change: it will not look any different yet.
        RestartNotice.Visibility = Visibility.Visible;
        RestartNowButton.Visibility = _restartApp is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnRestartForTheme(object sender, RoutedEventArgs e) => _restartApp?.Invoke();
}
