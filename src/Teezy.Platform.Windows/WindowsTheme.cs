using Microsoft.Win32;

namespace Teezy.Platform.Windows;

/// <summary>Whether Windows' own apps are set to the light theme, for Appearance ▸ Match Windows.</summary>
public static class WindowsTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// Settings ▸ Personalisation ▸ Colours ▸ "Choose your mode" for apps, not the taskbar (that
    /// is a separate value, <c>SystemUsesLightTheme</c>). Defaults to dark — TeezyFlow's own
    /// look — if the key is missing or unreadable, rather than guessing light.
    /// </summary>
    public static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
