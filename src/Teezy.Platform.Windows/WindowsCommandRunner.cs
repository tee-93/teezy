using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using Teezy.Core.Commands;
using static Teezy.Platform.Windows.Native;

namespace Teezy.Platform.Windows;

/// <summary>Carries out assistant commands on this machine.</summary>
/// <remarks>
/// <para>
/// The only place in Teezy that acts on the computer rather than typing into it, which is why
/// the set it can perform is closed and typed rather than a string it executes. Nothing here
/// takes a command it was not given by <see cref="CommandMatcher"/>.
/// </para>
/// <para>
/// Every failure is a <see cref="CommandFailedException"/> with something a person can act on.
/// "Couldn't find Chrome" and "I can't do that yet" look identical from the outside otherwise,
/// and they call for completely different responses.
/// </para>
/// </remarks>
public sealed class WindowsCommandRunner : ICommandRunner
{
    private readonly Lazy<IReadOnlyList<InstalledApp>> _apps =
        new(InstalledApps.All, LazyThreadSafetyMode.ExecutionAndPublication);

    public Task<string> RunAsync(VoiceCommand command, CancellationToken ct = default) =>
        Task.FromResult(command switch
        {
            VoiceCommand.LaunchApp app => Launch(app.Query),
            VoiceCommand.SetVolume v => SetVolume(v.Percent),
            VoiceCommand.AdjustVolume v => AdjustVolume(v.Delta),
            VoiceCommand.Mute m => SetMute(m.On),
            VoiceCommand.Media m => SendMedia(m.Key),
            VoiceCommand.LockScreen => Lock(),
            _ => throw new CommandFailedException("I don’t know how to do that."),
        });

    // ---- applications ----

    /// <summary>
    /// Brings an application to the front, starting it if it is not already running.
    /// </summary>
    /// <remarks>
    /// Focus first, launch second. "Open Outlook" when Outlook is already open means put it in
    /// front of me, not start a second one — and for apps that refuse to run twice, launching
    /// would do nothing visible at all.
    /// </remarks>
    private string Launch(string query)
    {
        if (Focus(query) is { } focused) return $"Switched to {focused}";

        var apps = _apps.Value;
        var name = AppNameMatcher.Best(query, apps.Select(a => a.Name))
                   ?? throw new CommandFailedException($"Couldn’t find {query}.");

        var app = apps.First(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        try
        {
            // Asking the shell to open the apps-folder entry is exactly what clicking it in
            // the Start menu does, and it is the only way to start a Store app, which has no
            // file to run at all.
            var start = app.LaunchesViaShell
                ? new ProcessStartInfo("explorer.exe", app.Target)
                : new ProcessStartInfo(app.Target) { UseShellExecute = true };

            Process.Start(start);
            return $"Opened {name}";
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new CommandFailedException($"Couldn’t open {name}: {e.Message}", e);
        }
    }

    /// <summary>The window title of whatever we brought forward, or null if nothing matched.</summary>
    private static string? Focus(string query)
    {
        Process[] running;
        try { running = Process.GetProcesses(); }
        catch (InvalidOperationException) { return null; }

        try
        {
            var windowed = running
                .Where(p => p.MainWindowHandle != 0)
                .ToDictionary(p => p.ProcessName, p => p, StringComparer.OrdinalIgnoreCase);

            if (AppNameMatcher.Best(query, windowed.Keys) is not { } match) return null;

            var target = windowed[match];
            var handle = target.MainWindowHandle;

            if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);

            // May be refused: Windows restricts which process can steal focus. Nothing to do
            // about it from here, and the window is at least restored either way.
            return SetForegroundWindow(handle) ? match : null;
        }
        finally
        {
            foreach (var p in running) p.Dispose();
        }
    }

    // ---- volume ----

    private static string SetVolume(int percent)
    {
        using var device = DefaultSpeakers();
        device.AudioEndpointVolume.MasterVolumeLevelScalar = percent / 100f;
        device.AudioEndpointVolume.Mute = false;
        return $"Volume {percent}%";
    }

    private static string AdjustVolume(int delta)
    {
        using var device = DefaultSpeakers();

        var now = (int)Math.Round(device.AudioEndpointVolume.MasterVolumeLevelScalar * 100);
        var wanted = Math.Clamp(now + delta, 0, 100);

        device.AudioEndpointVolume.MasterVolumeLevelScalar = wanted / 100f;
        if (wanted > 0) device.AudioEndpointVolume.Mute = false;

        return $"Volume {wanted}%";
    }

    private static string SetMute(bool on)
    {
        using var device = DefaultSpeakers();
        device.AudioEndpointVolume.Mute = on;
        return on ? "Muted" : "Unmuted";
    }

    /// <summary>
    /// The playback device, not the capture one.
    /// </summary>
    /// <remarks>
    /// <see cref="Role.Multimedia"/> rather than Communications: this is the volume of what you
    /// are listening to. The capture side deliberately uses the communications role instead,
    /// and mixing the two up would have the assistant turning down a microphone.
    /// </remarks>
    private static MMDevice DefaultSpeakers()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        catch (COMException e)
        {
            throw new CommandFailedException("No speakers to adjust.", e);
        }
    }

    // ---- media and lock ----

    private static string SendMedia(MediaKey key)
    {
        var vk = key switch
        {
            MediaKey.Next => VK_MEDIA_NEXT_TRACK,
            MediaKey.Previous => VK_MEDIA_PREV_TRACK,
            _ => VK_MEDIA_PLAY_PAUSE,
        };

        INPUT[] input =
        [
            new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } },
            new()
            {
                type = INPUT_KEYBOARD,
                u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } },
            },
        ];

        if (SendInput((uint)input.Length, input, Marshal.SizeOf<INPUT>()) == 0)
        {
            throw new CommandFailedException("Couldn’t reach the media keys.");
        }

        // Said as the key, not as the outcome: whatever is playing decides what it did, and
        // claiming "Playing" when it in fact paused would be worse than saying nothing.
        return key switch
        {
            MediaKey.Next => "Next track",
            MediaKey.Previous => "Previous track",
            _ => "Play / pause",
        };
    }

    private static string Lock()
    {
        if (!LockWorkStation()) throw new CommandFailedException("Windows refused to lock.");
        return "Locking";
    }
}
