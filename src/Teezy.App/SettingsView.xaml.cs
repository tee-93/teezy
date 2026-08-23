using Teezy.Cleanup;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Teezy.Core;
using Teezy.Core.Abstractions;
using Teezy.Core.Formatting;
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
    private readonly Func<IReadOnlyList<string>>? _knownApps;

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
        ClaudeFormatter? claude = null,
        Func<IReadOnlyList<string>>? knownApps = null)
    {
        InitializeComponent();

        _read = read;
        _write = write;
        _transcriber = transcriber;
        _autostart = autostart ?? new UnsupportedAutostart();
        _capture = capture;
        _secrets = secrets;
        _claude = claude;
        _knownApps = knownApps;

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
        ShowSpeechOptions(settings);
        ShowLlmState();
        ShowAppRules(settings);

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

        var wanted = AutostartBox.IsChecked == true;

        try
        {
            if (wanted) _autostart.Enable();
            else _autostart.Disable();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // A managed machine can refuse writes to the Run key outright. Saying so beats a
            // switch that flicks back on its own, which reads as the app being broken.
            ShowAutostartState();
            ExplainAutostart($"Windows would not let Teezy change the startup entry — {ex.Message}");
            return;
        }

        // Read back rather than trusting the write: the registry call can fail silently under
        // a restrictive policy, and the switch should show what is actually true.
        ShowAutostartState();

        // A switch that springs back and says nothing is the worst of both worlds — it looks
        // like the click missed. If the OS did not end up where the user asked, say so.
        if (_autostart.IsEnabled != wanted)
        {
            ExplainAutostart(wanted
                ? "The startup entry did not stick. Something on this machine is preventing it — try Task Manager ▸ Startup."
                : "Teezy could not remove its startup entry.");
        }
    }

    private void ExplainAutostart(string message)
    {
        AutostartNote.Text = message;
        AutostartNote.Visibility = Visibility.Visible;
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
            foreach (var (_, label, _) in Styles) StylePicker.Items.Add(label);
        }

        var index = Array.FindIndex(LlmModels, m => m.Id == settings.LlmModel);
        LlmModelPicker.SelectedIndex = index >= 0 ? index : 1;
        ModelCost.Text = LlmModels[LlmModelPicker.SelectedIndex].Cost;

        var timeout = Array.IndexOf(LlmTimeouts, settings.LlmTimeoutSeconds);
        LlmTimeoutPicker.SelectedIndex = timeout >= 0 ? timeout : 2;

        var style = Array.FindIndex(Styles, s => s.Style == settings.WritingStyle);
        StylePicker.SelectedIndex = style >= 0 ? style : 0;
        StyleHint.Text = Styles[StylePicker.SelectedIndex].Hint;
        StyleInstructionBox.Text = settings.StyleInstruction ?? string.Empty;

        ShowKeyState();
    }

    /// <summary>
    /// Shows whether a key is stored, and which one.
    /// </summary>
    /// <remarks>
    /// The mask is the whole point. Saving clears the box — it is never pre-filled — so
    /// without something to show, a successful save and a save that did nothing look
    /// identical: an empty box either way. Printing the key's own last four characters means
    /// the confirmation is about the key you just pasted rather than a reassuring sentence.
    /// </remarks>
    private void ShowKeyState()
    {
        var hint = _secrets?.Describe(App.ApiKeyName);

        KeyStatus.Text = hint is not null
            ? $"Saved and encrypted for your Windows account · {hint}"
            : "No key saved yet — cleanup falls back to the offline rules.";
        ForgetKeyButton.Visibility = hint is not null ? Visibility.Visible : Visibility.Collapsed;
        ApiKeyBox.Password = string.Empty;
        SaveKeyButton.IsEnabled = false;
    }

    private void OnLlmToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _write(_read() with { LlmCleanupEnabled = LlmBox.IsChecked == true });
        ShowLlmState();
    }

    /// <summary>The styles, with what each one is actually for.</summary>
    /// <remarks>
    /// Described by outcome rather than by adjective. "Polished" tells you nothing on its
    /// own; "tightens waffle, keeps your voice" tells you whether it is the one you want.
    /// </remarks>
    private static readonly (WritingStyle Style, string Label, string Hint)[] Styles =
    [
        (WritingStyle.Faithful, "Faithful — your words",
            "Fixes the transcript and nothing else. The safe default."),
        (WritingStyle.Polished, "Polished — tightened",
            "Cuts waffle and repairs awkward phrasing, keeping your voice."),
        (WritingStyle.Formal, "Formal — professional",
            "Raises the register. Expands contractions, drops slang."),
        (WritingStyle.Casual, "Casual — relaxed",
            "Contractions, short sentences, plain words."),
    ];

    private void OnStyleChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || StylePicker.SelectedIndex < 0) return;

        var chosen = Styles[StylePicker.SelectedIndex];
        StyleHint.Text = chosen.Hint;
        _write(_read() with { WritingStyle = chosen.Style });
    }

    /// <remarks>
    /// On lost focus, not on every keystroke: this is persisted to disk and rides on every
    /// request, and saving a half-typed instruction would send it.
    /// </remarks>
    private void OnStyleInstructionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var typed = StyleInstructionBox.Text.Trim();
        var current = _read();
        var stored = current.StyleInstruction ?? string.Empty;
        if (typed == stored) return;

        _write(current with { StyleInstruction = typed.Length == 0 ? null : typed });
    }

    // ---- Per-app rules ----

    /// <summary>
    /// Rebuilds the rule list from settings.
    /// </summary>
    /// <remarks>
    /// Rows are built in code and thrown away on every change rather than data-bound. The
    /// list is short, edits are rare, and a rebuild cannot drift out of step with what was
    /// saved — which a two-way binding over a record list can, quietly.
    /// </remarks>
    private void ShowAppRules(TeezySettings settings)
    {
        RuleRows.Children.Clear();

        foreach (var rule in settings.AppRules)
        {
            RuleRows.Children.Add(BuildRuleRow(rule));
        }

        NoRules.Visibility = settings.AppRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Offer the apps already in history, minus the ones that have a rule. Typing a name
        // still works — the box is editable — but nobody should have to know that Outlook
        // reports itself as "OUTLOOK".
        var taken = settings.AppRules.Select(r => r.App).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var known = (_knownApps?.Invoke() ?? [])
            .Where(a => !taken.Contains(a))
            .OrderBy(a => a, StringComparer.OrdinalIgnoreCase)
            .ToList();

        NewRuleApp.ItemsSource = known;
        NewRuleApp.Text = string.Empty;
    }

    private UIElement BuildRuleRow(AppRule rule)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBlock
        {
            Text = rule.App,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (System.Windows.Media.Brush)FindResource("Ink"),
        };
        Grid.SetColumn(name, 0);
        grid.Children.Add(name);

        var style = new ComboBox { Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var (_, label, _) in Styles) style.Items.Add(label);
        style.SelectedIndex = Math.Max(0, Array.FindIndex(Styles, s => s.Style == rule.Style));
        style.SelectionChanged += (_, _) =>
        {
            if (_loading || style.SelectedIndex < 0) return;
            ReplaceRule(rule, rule with { Style = Styles[style.SelectedIndex].Style });
        };
        Grid.SetColumn(style, 1);
        grid.Children.Add(style);

        var instruction = new TextBox
        {
            Text = rule.Instruction ?? string.Empty,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Height = 30,
            ToolTip = "An extra line for this app only. Replaces the global one.",
        };
        instruction.LostFocus += (_, _) =>
        {
            if (_loading) return;
            var typed = instruction.Text.Trim();
            if (typed == (rule.Instruction ?? string.Empty)) return;
            ReplaceRule(rule, rule with { Instruction = typed.Length == 0 ? null : typed });
        };
        Grid.SetColumn(instruction, 2);
        grid.Children.Add(instruction);

        var remove = new Button
        {
            Content = "Remove",
            Style = (Style)FindResource("Quiet"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) => ReplaceRule(rule, null);
        Grid.SetColumn(remove, 3);
        grid.Children.Add(remove);

        return grid;
    }

    /// <summary>Swaps one rule for another, or drops it when <paramref name="replacement"/>
    /// is null. Order is preserved, because first match wins and the user can see the order.</summary>
    private void ReplaceRule(AppRule existing, AppRule? replacement)
    {
        var settings = _read();
        var rules = settings.AppRules.ToList();

        var index = rules.FindIndex(r => ReferenceEquals(r, existing) || r == existing);
        if (index < 0) return;

        if (replacement is null) rules.RemoveAt(index);
        else rules[index] = replacement;

        _write(settings with { AppRules = rules });
        ShowAppRules(_read());
    }

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var app = (NewRuleApp.Text ?? string.Empty).Trim();
        if (app.Length == 0) return;

        var settings = _read();
        if (settings.AppRules.Any(r => r.Matches(app))) return;

        // Appended, not inserted. First match wins, so adding to the top would silently
        // shadow a rule the user added earlier and can still see.
        var rules = settings.AppRules.ToList();
        rules.Add(new AppRule { App = app, Style = settings.WritingStyle });

        _write(settings with { AppRules = rules });
        ShowAppRules(_read());
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
    /// The box is still never pre-filled with the whole key — putting a usable credential
    /// back on screen buys nothing, and the only useful actions are replace and forget. The
    /// masked hint under it carries the part worth seeing.
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

        // Write is void and a store can accept bytes it cannot give back — a profile on a
        // network share, a DPAPI context that changed under us. ShowKeyState has just tried
        // to read the key back; if that failed, say so here rather than leaving up a message
        // that reads as "no key" and looks like the save silently did nothing.
        if (_secrets.Describe(App.ApiKeyName) is null)
        {
            KeyStatus.Text = "Saved, but it could not be read back — the key is not usable.";
        }
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

    private static readonly (DecodingMethod Method, string Label, string Hint)[] Decoders =
    [
        (DecodingMethod.Greedy, "Greedy — fastest",
            "Takes the best token at each step. What every timing in the README was measured against."),
        (DecodingMethod.BeamSearch, "Beam search — accurate",
            "Weighs several transcripts before choosing. Slower, better on unusual words, and required for dictionary hints."),
    ];

    private static readonly int[] BeamSizes = [2, 4, 8, 16];

    /// <remarks>
    /// Described by consequence, because the number means nothing on its own and the failure
    /// mode is not "it did not work" — it is the engine hearing your hinted words in audio
    /// that never contained them.
    /// </remarks>
    private static readonly (double Score, string Label, string Hint)[] HotwordStrengths =
    [
        (1.5, "Gentle", "Nudges toward your hints. Measured to change nothing on clean audio."),
        (2.5, "Firm", "Noticeably biases decoding. Can disturb punctuation around the hinted word."),
        (4.0, "Heavy", "Strong pull. Expect hinted words to appear where you did not say them."),
    ];

    private void OnDecodingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || DecodingPicker.SelectedIndex < 0) return;

        var chosen = Decoders[DecodingPicker.SelectedIndex];
        _write(_read() with { Decoding = chosen.Method });
        ShowSpeechOptions(_read());
    }

    private void ShowSpeechOptions(TeezySettings settings)
    {
        if (DecodingPicker.Items.Count == 0)
        {
            foreach (var (_, label, _) in Decoders) DecodingPicker.Items.Add(label);
            foreach (var size in BeamSizes) BeamPicker.Items.Add(size);
            foreach (var (_, label, _) in HotwordStrengths) HotwordPicker.Items.Add(label);
        }

        var decoder = Array.FindIndex(Decoders, d => d.Method == settings.Decoding);
        DecodingPicker.SelectedIndex = decoder >= 0 ? decoder : 0;
        DecodingHint.Text = Decoders[DecodingPicker.SelectedIndex].Hint;

        BeamOptions.Visibility = settings.Decoding == DecodingMethod.BeamSearch
            ? Visibility.Visible
            : Visibility.Collapsed;

        BeamPicker.SelectedItem = BeamSizes.Contains(settings.BeamSize) ? settings.BeamSize : 4;

        var strength = Array.FindIndex(HotwordStrengths, h => Math.Abs(h.Score - settings.HotwordScore) < 0.01);
        HotwordPicker.SelectedIndex = strength >= 0 ? strength : 0;
        HotwordHint.Text = HotwordStrengths[HotwordPicker.SelectedIndex].Hint;
    }

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
            BeamSize = BeamPicker.SelectedItem as int? ?? _read().BeamSize,
            HotwordScore = HotwordPicker.SelectedIndex >= 0
                ? HotwordStrengths[HotwordPicker.SelectedIndex].Score
                : _read().HotwordScore,
        });

        if (HotwordPicker.SelectedIndex >= 0)
        {
            HotwordHint.Text = HotwordStrengths[HotwordPicker.SelectedIndex].Hint;
        }
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
