using Teezy.Cleanup;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Teezy.Core;
using Teezy.Core.Abstractions;
using Teezy.Core.Hotkeys;
using Teezy.Speech;

namespace Teezy.App;

/// <summary>Settings, as a page in the window rather than a modal dialog.</summary>
/// <remarks>
/// <para>
/// A page, not a dialog, because settings here are browsed as often as they are changed —
/// people open them to check which key is bound or whether the model loaded. A modal window
/// that must be dismissed to see anything else is the wrong shape for that.
/// </para>
/// <para>
/// Every change applies and persists immediately. There is no OK or Cancel: with eight
/// switches and a picker, a save step is ceremony that only creates a way to lose work.
/// </para>
/// </remarks>
public partial class SettingsView : UserControl
{
    /// <summary>
    /// Combinations worth offering without recording one.
    /// </summary>
    /// <remarks>
    /// Ctrl+Win leads because it is genuinely clean: no character, no system action, and Ctrl
    /// suppresses the Start menu that Win alone would open on release. Nothing Shift-only is
    /// offered — holding Shift for eight seconds raises the Windows Filter Keys prompt, and a
    /// push-to-talk hold routinely runs longer.
    /// </remarks>
    private static readonly Hotkey[] Presets =
    [
        new(HotkeyKey.Control, HotkeyKey.Windows),
        new(HotkeyKey.Control, HotkeyKey.Alt),
        new(HotkeyKey.Alt, HotkeyKey.Windows),
        new(HotkeyKey.Control, HotkeyKey.Alt, HotkeyKey.Windows),
        new(HotkeyKey.RightControl),
        new(HotkeyKey.ScrollLock),
        new(HotkeyKey.Pause),
        new(HotkeyKey.F13),
    ];

    private readonly Func<TeezySettings> _read;
    private readonly Action<TeezySettings> _write;
    private readonly IAutostart _autostart;
    private readonly IHotkeyCapture? _capture;
    private readonly ParakeetTranscriber? _transcriber;
    private readonly ISecretStore? _secrets;
    private readonly ClaudeFormatter? _claude;

    /// <summary>Suppresses change events while controls are populated, so opening the page
    /// does not look like the user editing it.</summary>
    private bool _loading = true;

    public SettingsView(
        Func<TeezySettings> read,
        Action<TeezySettings> write,
        ParakeetTranscriber? transcriber,
        IAutostart? autostart,
        IHotkeyCapture? capture,
        ISecretStore? secrets = null,
        ClaudeFormatter? claude = null)
    {
        InitializeComponent();

        _read = read;
        _write = write;
        _transcriber = transcriber;
        _autostart = autostart ?? new UnsupportedAutostart();
        _capture = capture;
        _secrets = secrets;
        _claude = claude;

        RecordButton.IsEnabled = _capture is not null;

        foreach (var n in new[] { 1, 2, 4, 6, 8 }) ThreadPicker.Items.Add(n);

        var version = typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        AboutVersion.Text = $"Teezy {version} · {arch} · .NET {Environment.Version.ToString(2)}";

        Refresh();
        _loading = false;
    }

    public void Refresh()
    {
        var wasLoading = _loading;
        _loading = true;

        var settings = _read();

        PopulateHotkeys(settings);
        ThreadPicker.SelectedItem = settings.NumThreads;
        CleanupBox.IsChecked = settings.CleanupEnabled;
        HudBox.IsChecked = settings.ShowHud;
        SoundBox.IsChecked = settings.SoundEnabled;

        ShowAutostartState();
        ShowModelState();
        ShowLlmState();

        _loading = wasLoading;
    }

    // ---- Hotkey ----

    private void PopulateHotkeys(TeezySettings settings)
    {
        HotkeyDisplay.Text = settings.Hotkey.Display;

        KeyPicker.Items.Clear();
        var options = Presets.ToList();
        if (!options.Contains(settings.Hotkey))
        {
            // A recorded combination must stay selectable, or reopening settings would show —
            // and on the next change apply — a different hotkey than the one in force.
            options.Insert(0, settings.Hotkey);
        }

        foreach (var option in options) KeyPicker.Items.Add(option.Display);
        KeyPicker.Tag = options;
        KeyPicker.SelectedIndex = options.IndexOf(settings.Hotkey);

        ShowHotkeyWarnings(settings.Hotkey);
    }

    private void ShowHotkeyWarnings(Hotkey hotkey)
    {
        var warnings = hotkey.Warnings;
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
        }));
    }

    private void EndRecording()
    {
        RecordButton.Content = "Record my own";
        HotkeyHint.Text = "Hold every key together, speak, then let go.";
        KeyPicker.IsEnabled = true;
    }

    private void ApplyHotkey(Hotkey hotkey)
    {
        var settings = _read();
        if (hotkey.IsEmpty || hotkey == settings.Hotkey)
        {
            ShowHotkeyWarnings(settings.Hotkey);
            return;
        }

        _write(settings with { Hotkey = hotkey });
        Refresh();
    }

    // ---- Autostart ----

    /// <summary>Reads autostart from the OS rather than from saved settings.</summary>
    /// <remarks>
    /// Always live. Windows lets the user disable a startup entry in Task Manager, and a
    /// switch mirrored from <c>settings.json</c> would keep showing "on" next to something
    /// that no longer happens.
    /// </remarks>
    private void ShowAutostartState()
    {
        AutostartBox.IsChecked = _autostart.IsEnabled;

        if (_autostart.IsBlockedByUser)
        {
            AutostartNote.Text = "Turned off in Task Manager’s Startup tab. Switching this on will re-enable it.";
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
        // a restrictive policy, and the switch should show what is actually true.
        ShowAutostartState();
    }

    // ---- Smarter cleanup ----

    /// <summary>Models offered, with the monthly cost at a realistic dictation volume.</summary>
    /// <remarks>
    /// The cost is shown rather than buried in a doc because it is the whole reason someone
    /// hesitates here, and because it is small enough that seeing it usually settles the
    /// question. Figures are per 400 dictations — roughly a month of ordinary use.
    /// </remarks>
    private static readonly (string Id, string Label, string Cost)[] LlmModels =
    [
        ("claude-haiku-4-5", "Haiku 4.5 — fastest", "About $0.20 a month, and the smallest delay."),
        ("claude-sonnet-5", "Sonnet 5 — balanced", "About $0.60 a month. Better at lists and spoken corrections."),
        ("claude-opus-5", "Opus 5 — best quality", "About $1 a month, and the slowest of the three."),
    ];

    private static readonly int[] LlmTimeouts = [3, 4, 6, 10, 15];

    private void ShowLlmState()
    {
        var settings = _read();

        LlmBox.IsChecked = settings.LlmCleanupEnabled;
        LlmDetail.Visibility = settings.LlmCleanupEnabled ? Visibility.Visible : Visibility.Collapsed;

        if (LlmModelPicker.Items.Count == 0)
        {
            foreach (var (_, label, _) in LlmModels) LlmModelPicker.Items.Add(label);
            foreach (var seconds in LlmTimeouts) LlmTimeoutPicker.Items.Add($"{seconds} seconds");
        }

        var index = Array.FindIndex(LlmModels, m => m.Id == settings.LlmModel);
        LlmModelPicker.SelectedIndex = index >= 0 ? index : 1;
        ModelCost.Text = LlmModels[LlmModelPicker.SelectedIndex].Cost;

        var timeout = Array.IndexOf(LlmTimeouts, settings.LlmTimeoutSeconds);
        LlmTimeoutPicker.SelectedIndex = timeout >= 0 ? timeout : 2;

        ShowKeyState();
    }

    private void ShowKeyState()
    {
        var stored = _secrets?.Has(App.ApiKeyName) == true;

        KeyStatus.Text = stored
            ? "A key is saved and encrypted for your Windows account."
            : "No key saved yet — cleanup falls back to the offline rules.";
        ForgetKeyButton.Visibility = stored ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyBox.Password = string.Empty;
        SaveKeyButton.IsEnabled = false;
    }

    private void OnLlmToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _write(_read() with { LlmCleanupEnabled = LlmBox.IsChecked == true });
        ShowLlmState();
    }

    private void OnLlmModelChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || LlmModelPicker.SelectedIndex < 0) return;

        var chosen = LlmModels[LlmModelPicker.SelectedIndex];
        ModelCost.Text = chosen.Cost;
        _write(_read() with { LlmModel = chosen.Id });
    }

    /// <summary>Enables Save only once something has been typed.</summary>
    /// <remarks>
    /// The box is deliberately never pre-filled with the stored key. Reading a secret back
    /// out of the store just to display it — even masked — puts it in memory and on screen
    /// for no benefit; the only useful actions are replace and forget.
    /// </remarks>
    private void OnApiKeyTyped(object sender, RoutedEventArgs e) =>
        SaveKeyButton.IsEnabled = ApiKeyBox.Password.Trim().Length > 0;

    private void OnSaveApiKey(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        if (key.Length == 0 || _secrets is null) return;

        _secrets.Write(App.ApiKeyName, key);
        TestResult.Text = string.Empty;
        ShowKeyState();
    }

    private void OnForgetApiKey(object sender, RoutedEventArgs e)
    {
        _secrets?.Delete(App.ApiKeyName);
        TestResult.Text = string.Empty;
        ShowKeyState();
    }

    /// <summary>Sends one short, deliberately messy sentence and shows what comes back.</summary>
    /// <remarks>
    /// Worth a real round trip rather than only validating the key: it proves the whole path
    /// — key, network, model availability, and the plausibility guard — and shows the latency
    /// the user is signing up for on every utterance.
    /// </remarks>
    private async void OnTestLlm(object sender, RoutedEventArgs e)
    {
        if (_claude is null) return;

        TestButton.IsEnabled = false;
        TestResult.Text = "Asking Claude…";

        const string messy = "um so i was thinking we could maybe ship on friday scratch that monday";

        try
        {
            var result = await _claude.FormatAsync(messy);
            var outcome = _claude.LastOutcome;

            TestResult.Text = outcome?.UsedClaude == true
                ? $"“{result}”  ({outcome.Milliseconds:0} ms)"
                : $"Fell back to the offline rules — {outcome?.Problem ?? "unknown reason"}";
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    // ---- Model ----

    private void ShowModelState()
    {
        var loaded = _transcriber?.IsLoaded == true;

        ModelDot.Fill = new SolidColorBrush(loaded ? Brand.Accent : Brand.Faint);
        ModelStatus.Text = loaded
            ? $"Parakeet TDT 0.6B v2 · ready in {_transcriber!.LoadTime.TotalSeconds:F1} s"
            : "Not loaded yet.";
        ModelPath.Text = _transcriber?.Paths?.Directory ?? ModelPaths.DefaultDirectory;
    }

    // ---- Everything else ----

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _write(_read() with
        {
            NumThreads = ThreadPicker.SelectedItem as int? ?? _read().NumThreads,
            LlmTimeoutSeconds = LlmTimeoutPicker.SelectedIndex >= 0
                ? LlmTimeouts[LlmTimeoutPicker.SelectedIndex]
                : _read().LlmTimeoutSeconds,
            CleanupEnabled = CleanupBox.IsChecked == true,
            ShowHud = HudBox.IsChecked == true,
            SoundEnabled = SoundBox.IsChecked == true,
        });
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(TeezySettings.DefaultPath)!;
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void OnQuit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    /// <summary>Cancels a running capture when the page is navigated away from.</summary>
    /// <remarks>
    /// A capture left active would swallow the next hotkey press, because the source routes
    /// key events to the capture rather than to dictation while one is running.
    /// </remarks>
    public void Leaving() => _capture?.CancelCapture();
}
