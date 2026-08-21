using System.Threading.Tasks;
using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Wisper.App;

/// <summary>Makes a fatal error visible instead of letting the app vanish.</summary>
/// <remarks>
/// A tray app with no main window dies silently: the icon disappears and nothing explains
/// why. That happened for real — globalization-invariant mode is incompatible with WPF and
/// killed the process the first time the HUD rendered text, with the only evidence buried in
/// the Windows event log. Everything here exists so the next such failure names itself.
/// </remarks>
internal static class CrashLog
{
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Wisper", "crash.log");

    public static void Install(Application app)
    {
        // Exceptions on the UI thread. Handled, so the app can keep running when the failure
        // is confined to one window.
        app.DispatcherUnhandledException += (_, e) =>
        {
            Write("Dispatcher", e.Exception);
            e.Handled = true;
            Report(e.Exception);
        };

        // Anything else. Not recoverable — the runtime is already tearing down — so this
        // only records it.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("AppDomain", e.ExceptionObject as Exception);

        // A faulted Task nobody awaited. Observed so it cannot escalate.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Task", e.Exception);
            e.SetObserved();
        };
    }

    private static void Write(string source, Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var entry = new StringBuilder()
                .AppendLine($"--- {DateTimeOffset.Now:u} [{source}] ---")
                .AppendLine(ex.ToString())
                .AppendLine();
            File.AppendAllText(Path, entry.ToString());
        }
        catch (IOException)
        {
            // Failing to record a crash must not itself crash.
        }
    }

    private static void Report(Exception ex) =>
        MessageBox.Show(
            $"{ex.GetType().Name}: {ex.Message}\n\nFull detail: {Path}",
            "Wisper hit an error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
}
