using System.Collections.Generic;
using Teezy.Core.Hotkeys;
using System;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using Teezy.Core;
using Teezy.Core.Abstractions;
using Teezy.Core.Dictionary;
using Teezy.Speech;

namespace Teezy.App;

public partial class SettingsWindow : Window
{
    private TeezySettings _settings;

    /// <summary>Suppresses change events while the controls are being populated, so loading
    /// the window does not look like the user editing it.</summary>
    private bool _loading = true;

    public event Action<TeezySettings>? SettingsChanged;

    private readonly IAutostart _autostart;
    private readonly IHotkeyCapture? _capture;

    /// <summary>
    /// Combinations worth offering without recording one.
    /// </summary>
    /// <remarks>
    /// Ctrl+Win leads because it is what this is modelled on and it is genuinely clean: no
    /// character, no system action, and Ctrl suppresses the Start menu that Win alone would
    /// open on release. Nothing Shift-only is offered — holding Shift for eight seconds
    /// raises the Windows Filter Keys prompt, and a push-to-talk hold routinely runs longer.
    /// </remarks>
    private static readonly Hotkey[] Presets =
    [
        new(HotkeyKey.Control, HotkeyKey.Windows),
        new(HotkeyKey.Control, HotkeyKey.Alt),
        new(HotkeyKey.Alt, HotkeyKey.Windows),
        new(HotkeyKey.RightControl),
        new(HotkeyKey.ScrollLock),
        new(HotkeyKey.Pause),
        new(HotkeyKey.F13),
    ];

    public SettingsWindow(
        TeezySettings settings,
        ParakeetTranscriber? transcriber,
        bool modelReady,
        IAutostart? autostart = null,
        IHotkeyCapture? capture = null)
    {
        InitializeComponent();
        _settings = settings;
        _autostart = autostart ?? new UnsupportedAutostart();
        _capture = capture;

        Subtitle.Text = "On-device dictation. Nothing leaves this machine.";

        RecordButton.IsEnabled = _capture is not null;
        PopulateHotkeys();

        foreach (var n in new[] { 1, 2, 4, 6, 8 }) ThreadPicker.Items.Add(n);
        ThreadPicker.SelectedItem = settings.NumThreads;

        CleanupBox.IsChecked = settings.CleanupEnabled;
        HudBox.IsChecked = settings.ShowHud;
        SoundBox.IsChecked = settings.SoundEnabled;

        if (modelReady && transcriber?.Paths is { } paths)
        {
            ModelStatus.Text =
                $"Parakeet TDT 0.6B v2 · loaded in {transcriber.LoadTime.TotalSeconds:F1} s";
            ModelPath.Text = paths.Directory;
        }
        else
        {
            ModelStatus.Text = "Not loaded.";
            ModelPath.Text = ModelPaths.DefaultDirectory;
        }

        ShowAutostartState();

        _loading = false;
    }

    /// <summary>Reads autostart from the OS rather than from saved settings.</summary>
    /// <remarks>
    /// Always read live. Windows lets the user disable a startup entry in Task Manager, and a
    /// checkbox mirrored from <c>settings.json</c> would keep showing a tick next to something
    /// that no longer happens.
    /// </remarks>
    private void ShowAutostartState()
    {
        AutostartBox.IsChecked = _autostart.IsEnabled;

        if (_autostart.IsBlockedByUser)
        {
            AutostartNote.Text =
                "Turned off in Task Manager’s Startup tab. Ticking this will switch it back on.";
            AutostartNote.Visibility = Visibility.Visible;
        }
        else
        {
            AutostartNote.Visibility = Visibility.Collapsed;
        }
    }

    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        if (AutostartBox.IsChecked == true) _autostart.Enable();
        else _autostart.Disable();

        // Read back rather than trusting the write: the registry call can fail silently under
        // a restrictive policy, and the checkbox should show what is actually true.
        ShowAutostartState();
    }

    // ---- Hotkey ----

    /// <summary>Fills the picker with the presets plus, if it is not one, the current choice.</summary>
    private void PopulateHotkeys()
    {
        var wasLoading = _loading;
        _loading = true;

        KeyPicker.Items.Clear();

        var options = Presets.ToList();
        if (!options.Contains(_settings.Hotkey))
        {
            // A recorded combination must still be selectable, or reopening Settings would
            // silently show — and on the next change, apply — a different hotkey.
            options.Insert(0, _settings.Hotkey);
        }

        foreach (var option in options) KeyPicker.Items.Add(option.Display);
        KeyPicker.Tag = options;
        KeyPicker.SelectedIndex = options.IndexOf(_settings.Hotkey);

        ShowHotkeyWarnings();
        _loading = wasLoading;
    }

    private void ShowHotkeyWarnings()
    {
        var warnings = _settings.Hotkey.Warnings;
        HotkeyWarningText.Text = string.Join(" ", warnings);
        HotkeyWarning.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHotkeyPreset(object sender, RoutedEventArgs e)
    {
        if (_loading || KeyPicker.Tag is not List<Hotkey> options) return;
        if (KeyPicker.SelectedIndex < 0 || KeyPicker.SelectedIndex >= options.Count) return;

        ApplyHotkey(options[KeyPicker.SelectedIndex]);
    }

    private void OnRecordHotkey(object sender, RoutedEventArgs e)
    {
        if (_capture is null) return;

        if (_capture.IsCapturing)
        {
            _capture.CancelCapture();
            EndRecording();
            return;
        }

        RecordButton.Content = "Cancel";
        HotkeyHint.Text = "Hold the keys you want, then let go.";
        KeyPicker.IsEnabled = false;

        _capture.BeginCapture(hotkey => Dispatcher.Invoke(() =>
        {
            EndRecording();
            ApplyHotkey(hotkey);
            PopulateHotkeys();
        }));
    }

    private void EndRecording()
    {
        RecordButton.Content = "Record…";
        HotkeyHint.Text = "Hold every key together, speak, release.";
        KeyPicker.IsEnabled = true;
    }

    private void ApplyHotkey(Hotkey hotkey)
    {
        if (hotkey.IsEmpty || hotkey == _settings.Hotkey)
        {
            ShowHotkeyWarnings();
            return;
        }

        _settings = _settings with { Hotkey = hotkey };
        ShowHotkeyWarnings();
        SettingsChanged?.Invoke(_settings);
    }

    protected override void OnClosed(EventArgs e)
    {
        // A capture left running would swallow the next hotkey press, because the source
        // routes events to the capture instead of to dictation while one is active.
        _capture?.CancelCapture();
        base.OnClosed(e);
    }

    // ---- Everything else ----

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings = _settings with
        {
            NumThreads = ThreadPicker.SelectedItem as int? ?? _settings.NumThreads,
            CleanupEnabled = CleanupBox.IsChecked == true,
            ShowHud = HudBox.IsChecked == true,
            SoundEnabled = SoundBox.IsChecked == true,
        };

        SettingsChanged?.Invoke(_settings);
    }

    private void OnEditDictionary(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(DictionaryStore.DefaultPath) { UseShellExecute = true });

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
