using System.Diagnostics;
using Microsoft.Win32;
using Teezy.Core.Abstractions;

namespace Teezy.Platform.Windows;

/// <summary>Runs Teezy at sign-in via the per-user Run key.</summary>
/// <remarks>
/// <para>
/// <c>HKCU</c>, never <c>HKLM</c>: this needs no administrator rights, applies to one user,
/// and — the part that matters — appears in Task Manager's Startup tab, where people already
/// expect to manage startup apps. A scheduled task would hide it from the place users look.
/// </para>
/// <para>
/// <b>Writing the Run value alone is not enough.</b> If the user has ever disabled Teezy in
/// Task Manager, Windows records that in a separate <c>StartupApproved</c> key which takes
/// precedence — so enabling would appear to succeed and nothing would happen at sign-in.
/// <see cref="Enable"/> clears that veto; <see cref="IsEnabled"/> honours it.
/// </para>
/// </remarks>
public sealed class WindowsAutostart : IAutostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Where Explorer records startup entries the user switched off.</summary>
    private const string ApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private const string ValueName = "Teezy";

    /// <summary>
    /// Passed on the sign-in launch, and only then.
    /// </summary>
    /// <remarks>
    /// It is how the app tells "Windows started me" from "the user started me". A tray app
    /// that opens its window at sign-in is a nuisance; one that opens nothing when you
    /// double-click it looks broken. The two cases need different behaviour, and the command
    /// line is the only thing that distinguishes them.
    /// </remarks>
    public const string StartupFlag = "--startup";

    /// <summary>
    /// The full command line to register. Quoted: the path contains spaces on any machine
    /// whose user name does, and an unquoted one is parsed as a command plus arguments.
    /// </summary>
    private static string Command(string exe) => $"\"{exe}\" {StartupFlag}";

    /// <summary>
    /// The executable to launch.
    /// </summary>
    /// <remarks>
    /// <c>Environment.ProcessPath</c>, not <c>Assembly.Location</c>. In a single-file build
    /// the managed assembly is extracted to a temporary folder, so <c>Assembly.Location</c>
    /// is empty or points somewhere that will be cleaned up — registering it would produce a
    /// startup entry that silently stops working.
    /// </remarks>
    private static string? ExecutablePath => Environment.ProcessPath;

    public bool IsEnabled => RegisteredPath() is not null && !IsBlockedByUser;

    public bool IsBlockedByUser
    {
        get
        {
            if (RegisteredPath() is null) return false;

            using var key = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            if (key?.GetValue(ValueName) is not byte[] { Length: > 0 } state) return false;

            // A 12-byte blob whose first byte carries the flag: 0x02 enabled, 0x03 disabled.
            // Testing the low bit rather than comparing to 0x03 exactly, because Windows has
            // used other even/odd pairs here across versions.
            return (state[0] & 0x01) != 0;
        }
    }

    public void Enable()
    {
        if (ExecutablePath is not { Length: > 0 } exe) return;

        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            run?.SetValue(ValueName, Command(exe), RegistryValueKind.String);
        }

        ClearUserVeto();
    }

    public void Disable()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);

        // The approval entry is left alone. Removing it would discard the user's own Task
        // Manager choice, which should outlive us switching the feature off.
    }

    /// <summary>
    /// Brings the startup entry back into line with this executable, if one is registered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called at launch. Without it, moving or republishing the exe leaves a Run value
    /// pointing at a file that no longer exists — and the failure is invisible, because
    /// nothing reports a startup entry that did not resolve.
    /// </para>
    /// <para>
    /// It compares the <b>whole command line</b>, not just the path, so an entry written
    /// before <see cref="StartupFlag"/> existed is upgraded in place rather than left to open
    /// the window at every sign-in forever.
    /// </para>
    /// </remarks>
    public void RefreshPathIfRegistered()
    {
        if (ExecutablePath is not { Length: > 0 } exe) return;
        if (RegisteredValue() is not { } current) return;

        var wanted = Command(exe);
        if (string.Equals(current.Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return;

        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run?.SetValue(ValueName, wanted, RegistryValueKind.String);
        Debug.WriteLine($"autostart entry updated: {current} -> {wanted}");
    }

    /// <summary>The raw Run value, or null if there is none.</summary>
    private static string? RegisteredValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string { Length: > 0 } value ? value : null;
    }

    /// <summary>The executable out of the registered command line, or null.</summary>
    private static string? RegisteredPath()
    {
        if (RegisteredValue() is not { } value) return null;

        var trimmed = value.Trim();

        // A command line, not a path: "C:\...\Teezy.exe" --startup. Take what is between the
        // quotes; fall back to the whole string for entries written before the flag existed.
        if (trimmed.StartsWith('"'))
        {
            var close = trimmed.IndexOf('"', 1);
            if (close > 1) return trimmed[1..close];
        }

        trimmed = trimmed.Trim('"');
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static void ClearUserVeto()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
