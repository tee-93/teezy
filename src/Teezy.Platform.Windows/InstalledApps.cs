namespace Teezy.Platform.Windows;

/// <summary>One thing the user could ask to be opened.</summary>
/// <param name="Name">What it is called, and therefore what someone would say.</param>
/// <param name="Target">
/// Either a shortcut path or a <c>shell:AppsFolder\…</c> identifier. Which one decides how it
/// is started, so the distinction survives to <see cref="WindowsCommandRunner"/>.
/// </param>
internal sealed record InstalledApp(string Name, string Target)
{
    internal const string AppsFolderPrefix = @"shell:AppsFolder\";

    /// <summary>
    /// True when this must be started through the shell rather than by running a file.
    /// </summary>
    /// <remarks>
    /// Not the same as "packaged", which is what this was first called and was wrong: the apps
    /// folder lists desktop applications alongside Store ones, so Chrome arrives this way too.
    /// What it actually distinguishes is how the thing is launched.
    /// </remarks>
    internal bool LaunchesViaShell => Target.StartsWith(AppsFolderPrefix, StringComparison.Ordinal);
}

/// <summary>What is installed, by the name a person would say.</summary>
/// <remarks>
/// <para>
/// <b>The apps folder, not the Start menu directories.</b> Enumerating <c>*.lnk</c> misses
/// every packaged app — measured on this machine, that meant Notepad, Calculator, Teams and
/// the current Outlook all resolved to nothing, because Store apps have no shortcut file. The
/// <c>shell:AppsFolder</c> namespace lists packaged and desktop applications together, which
/// is precisely the list the Start menu itself shows.
/// </para>
/// <para>
/// Shortcut enumeration survives as the fallback for when the shell refuses to talk to us,
/// since a degraded list beats an empty one.
/// </para>
/// <para>
/// Read once and cached for the life of the process. Installing something and immediately
/// asking for it by voice is rare enough to be worth a restart; walking the shell on every
/// command is not.
/// </para>
/// </remarks>
internal static class InstalledApps
{
    internal static IReadOnlyList<InstalledApp> All()
    {
        var fromShell = FromAppsFolder();
        return fromShell.Count > 0 ? fromShell : FromStartMenu();
    }

    /// <summary>
    /// Everything the Start menu would show, via the shell.
    /// </summary>
    /// <remarks>
    /// On its own STA thread deliberately. Shell COM is apartment-sensitive and this is called
    /// from whichever pool thread happened to run the command, which is exactly the situation
    /// that produces intermittent failures nobody can reproduce.
    /// </remarks>
    private static List<InstalledApp> FromAppsFolder()
    {
        var found = new List<InstalledApp>();

        var thread = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type is null) return;

                dynamic? shell = Activator.CreateInstance(type);
                if (shell is null) return;

                dynamic folder = shell.NameSpace("shell:AppsFolder");
                foreach (dynamic item in folder.Items())
                {
                    string name = item.Name;
                    string target = item.Path;

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(target)) continue;
                    if (IsNoise(name)) continue;

                    found.Add(new InstalledApp(name, InstalledApp.AppsFolderPrefix + target));
                }
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException
                                          or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
                                          or MissingMethodException
                                          or UnauthorizedAccessException)
            {
                // Fall back to shortcuts rather than leaving the assistant unable to open
                // anything at all.
                found.Clear();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        // Generous, and bounded: a hung shell must not hang a voice command forever.
        if (!thread.Join(TimeSpan.FromSeconds(10))) return [];

        return found;
    }

    private static List<InstalledApp> FromStartMenu()
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in Roots())
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> links;
            try
            {
                links = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var link in links)
            {
                var name = Path.GetFileNameWithoutExtension(link);
                if (name.Length == 0 || IsNoise(name)) continue;

                // The user's own Start menu is walked first, so a personal shortcut beats the
                // all-users one of the same name.
                found.TryAdd(name, link);
            }
        }

        return [.. found.Select(f => new InstalledApp(f.Key, f.Value))];
    }

    private static IEnumerable<string> Roots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
    }

    /// <summary>Entries that sit beside an application and compete for its spoken name.</summary>
    private static bool IsNoise(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("release notes", StringComparison.OrdinalIgnoreCase)
        || name.Contains("documentation", StringComparison.OrdinalIgnoreCase)
        || name.Contains("readme", StringComparison.OrdinalIgnoreCase)
        || name.Contains("website", StringComparison.OrdinalIgnoreCase);
}
