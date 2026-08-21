using System.Diagnostics;
using static Teezy.Platform.Windows.Native;

namespace Teezy.Platform.Windows;

/// <inheritdoc cref="Teezy.Core.Abstractions.IForegroundApp"/>
public sealed class WindowsForegroundApp : Teezy.Core.Abstractions.IForegroundApp
{
    public string? Current
    {
        get
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == 0) return null;

                _ = GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0) return null;

                using var process = Process.GetProcessById((int)pid);
                return Prettify(process.ProcessName);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                // The window's process can exit between the two calls. A missing label is
                // not worth failing a dictation over.
                return null;
            }
        }
    }

    /// <summary>Turns a process name into something worth showing in a list.</summary>
    private static string Prettify(string processName) => processName switch
    {
        "chrome" => "Chrome",
        "msedge" => "Edge",
        "firefox" => "Firefox",
        "Code" => "VS Code",
        "devenv" => "Visual Studio",
        "WindowsTerminal" => "Terminal",
        "olk" or "OUTLOOK" => "Outlook",
        "ms-teams" or "Teams" => "Teams",
        "slack" => "Slack",
        "Discord" => "Discord",
        "notepad" => "Notepad",
        "explorer" => "File Explorer",
        _ => processName,
    };
}
