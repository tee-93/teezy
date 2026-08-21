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
            // Quoted: the path routinely contains spaces, and an unquoted one is parsed as a
            // command plus arguments.
            run?.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
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
    /// Re-points the startup entry at the current executable if it has moved.
    /// </summary>
    /// <remarks>
    /// Called at launch. Without it, moving or republishing the exe leaves a Run value
    /// pointing at a file that no longer exists — and the failure is invisible, because
    /// nothing reports a startup entry that did not resolve.
    /// </remarks>
    public void RefreshPathIfRegistered()
    {
        if (ExecutablePath is not { Length: > 0 } exe) return;
        if (RegisteredPath() is not { } registered) return;

        if (string.Equals(registered, exe, StringComparison.OrdinalIgnoreCase)) return;

        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run?.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
        Debug.WriteLine($"autostart path updated: {registered} -> {exe}");
    }

    /// <summary>The registered executable path with any quoting removed, or null.</summary>
    private static string? RegisteredPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        if (key?.GetValue(ValueName) is not string value) return null;

        var trimmed = value.Trim().Trim('"');
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static void ClearUserVeto()
    {
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
