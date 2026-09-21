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
using System.Windows.Threading;
using Teezy.Core;
using Teezy.Core.Abstractions;
using Teezy.Core.Calendar;
using Teezy.Core.Formatting;
using Teezy.Core.Hotkeys;
using Teezy.Core.Speech;
using Teezy.Core.Voice;
using Teezy.Speech;
using Teezy.Connectors;
using System.Net.Http;
using System.Threading.Tasks;

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
    private readonly Func<IAudioCapture>? _microphone;
    private readonly ISpeaker? _speaker;
    private readonly VoiceUsage? _usage;
    private readonly ConnectedAccounts? _calendars;

    /// <summary>The capture opened by the level test, or null when no test is running.</summary>
    private IAudioCapture? _preview;
    private DispatcherTimer? _previewTimer;
    private DateTime _previewStarted;
    private float _previewLevel;
    private float _previewPeak;
    private int _micDeviceCount;

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
        Func<IReadOnlyList<string>>? knownApps = null,
        ISpeaker? speaker = null,
        VoiceUsage? usage = null,
        Func<IAudioCapture>? microphone = null,
        ConnectedAccounts? calendars = null)
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
        _microphone = microphone;
        _speaker = speaker;
        _usage = usage;
        _calendars = calendars;

        RecordButton.IsEnabled = _capture is not null;
        MicTestButton.IsEnabled = _microphone is not null;

        foreach (var n in new[] { 1, 2, 4, 6, 8 }) ThreadPicker.Items.Add(n);

        // Not "1.0.0" when it cannot be read. A plausible-looking default is what let About
        // report 1.0.0 through four releases without anyone noticing it was not the truth.
        var version = typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "unknown version";
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture;
        AboutVersion.Text = $"TeezyFlow {version} · {arch} · .NET {Environment.Version.ToString(2)}";

        Refresh();
        _loading = false;
    }

    /// <summary>Shows the chosen tab and hides the rest.</summary>
    /// <remarks>
    /// The panels are siblings in one Grid rather than a TabControl's items, because every
    /// control in them is reached by name from this file — a TabControl would put them behind
    /// lazily realised templates, and half of <c>Refresh</c> would start finding nulls.
    /// </remarks>
    private void OnTabChosen(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton tab || tab.Tag is not string chosen) return;
        if (TabHost is null) return;

        foreach (var panel in TabHost.Children.OfType<FrameworkElement>())
        {
            panel.Visibility = panel.Name == chosen ? Visibility.Visible : Visibility.Collapsed;
        }

        // Otherwise a tab opens at whatever depth the last one was scrolled to, which reads as
        // a page that has lost its top.
        TabScroll?.ScrollToTop();
    }

    public void Refresh()
    {
        var wasLoading = _loading;
        _loading = true;

        var settings = _read();

        PopulateHotkeys(settings);
        PopulateAssistant(settings);
        PopulateCalendar(settings);
        PopulateMicrophones(settings);
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

    // ---- Assistant ----

    /// <summary>What the assistant key can be set to. The first entry turns it off.</summary>
    /// <remarks>
    /// Ctrl+Win leads the offered combinations because it is the one most likely to be free:
    /// dictation defaults to it, but anyone who has moved dictation elsewhere has it spare, and
    /// it does not contain the other presets.
    /// </remarks>
    private void PopulateAssistant(TeezySettings settings)
    {
        var options = new List<Hotkey> { new() };
        options.AddRange(Presets.Where(p => p != settings.Hotkey));

        if (!settings.AssistantHotkey.IsEmpty && !options.Contains(settings.AssistantHotkey))
        {
            // A recorded combination must stay selectable, or reopening settings would show —
            // and on the next change apply — a different key than the one in force.
            options.Insert(1, settings.AssistantHotkey);
        }

        AssistantPicker.Items.Clear();
        foreach (var option in options)
        {
            AssistantPicker.Items.Add(option.IsEmpty ? "Off" : option.Display);
        }

        AssistantPicker.Tag = options;
        AssistantPicker.SelectedIndex = Math.Max(0, options.IndexOf(settings.AssistantHotkey));
        AssistantRecordButton.IsEnabled = _capture is not null;

        ShowAssistantState(settings);
    }

    /// <summary>
    /// What the assistant tier costs, said in the terms someone decides with.
    /// </summary>
    /// <remarks>
    /// Deliberately per-command rather than per-month, unlike the cleanup tier's. Cleanup runs
    /// on every utterance, so a monthly figure is meaningful; this runs only when the local
    /// patterns miss, and nobody can predict how often that will be for them.
    /// </remarks>
    private static readonly (string Id, string Label, string Cost)[] AssistantModels =
    [
        ("claude-haiku-4-5", "Haiku 4.5 — fastest", "A fraction of a cent per question, and the least waiting."),
        ("claude-sonnet-5", "Sonnet 5 — balanced", "Roughly three times Haiku, and better at odd phrasing."),
        ("claude-opus-5", "Opus 5 — best quality", "The dearest and the slowest. Rarely worth it for one-line commands."),
    ];

    private void ShowAssistantLlm(TeezySettings settings)
    {
        if (AssistantModelPicker.Items.Count == 0)
        {
            foreach (var (_, label, _) in AssistantModels) AssistantModelPicker.Items.Add(label);
        }

        AssistantLlmBox.IsChecked = settings.AssistantLlmEnabled;
        SpeakBox.IsChecked = settings.SpeakAnswers;
        AssistantLlmDetail.Visibility = settings.AssistantLlmEnabled ? Visibility.Visible : Visibility.Collapsed;

        var index = Array.FindIndex(AssistantModels, m => m.Id == settings.AssistantModel);
        AssistantModelPicker.SelectedIndex = index >= 0 ? index : 0;
        AssistantModelCost.Text = AssistantModels[AssistantModelPicker.SelectedIndex].Cost;
    }

    private void OnAssistantLlmToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _write(_read() with { AssistantLlmEnabled = AssistantLlmBox.IsChecked == true });
        Refresh();
    }

    private void OnSpeakToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _write(_read() with { SpeakAnswers = SpeakBox.IsChecked == true });
        Refresh();
    }

    /// <summary>One row of the voice picker. Null name means "pick the best available".</summary>
    private sealed record VoiceChoice(string? Id, string Label);

    private static readonly (VoiceProvider Provider, string Label, string Hint)[] VoiceProviders =
    [
        (VoiceProvider.Windows, "Windows — free",
            "The voices already on this machine. Free, offline, and instant."),
        (VoiceProvider.ElevenLabs, "ElevenLabs — paid",
            "Much better to listen to. Costs a subscription, and waits for the network before it starts."),
    ];

    private void PopulateVoices(TeezySettings settings)
    {
        VoiceDetail.Visibility = settings.SpeakAnswers ? Visibility.Visible : Visibility.Collapsed;
        if (!settings.SpeakAnswers || _speaker is null) return;

        if (VoiceProviderPicker.Items.Count == 0)
        {
            foreach (var (_, label, _) in VoiceProviders) VoiceProviderPicker.Items.Add(label);
        }

        var provider = Array.FindIndex(VoiceProviders, p => p.Provider == settings.VoiceProvider);
        VoiceProviderPicker.SelectedIndex = provider >= 0 ? provider : 0;
        VoiceProviderHint.Text = VoiceProviders[VoiceProviderPicker.SelectedIndex].Hint;

        var paid = settings.VoiceProvider == VoiceProvider.ElevenLabs;
        ElevenLabsDetail.Visibility = paid ? Visibility.Visible : Visibility.Collapsed;
        if (paid) ShowElevenKeyState();

        var voices = _speaker.Voices();
        var rows = new List<VoiceChoice>();

        // No automatic row for the paid tier: its voices belong to an account rather than a
        // fixed set, so there is nothing sensible to fall back to and an unchosen voice means
        // the tier simply is not ready.
        if (!paid)
        {
            rows.Add(new VoiceChoice(
                null, _speaker.VoiceName is { } current ? $"Automatic — {current}" : "Automatic"));
        }

        // Newer voices first, then by language, because the older SAPI5 set is worse in a way
        // nobody has ever wanted and it should not be what the eye lands on.
        rows.AddRange(voices
            .OrderByDescending(v => v.IsModern)
            .ThenBy(v => v.Culture, StringComparer.CurrentCulture)
            .ThenBy(v => v.Name, StringComparer.CurrentCulture)
            .Select(v => new VoiceChoice(
                v.Id,
                v.IsModern
                    ? $"{Shorten(v.Name)} — {v.Culture}"
                    : $"{Shorten(v.Name)} — {v.Culture} (older)")));

        VoicePicker.Items.Clear();
        foreach (var row in rows) VoicePicker.Items.Add(row.Label);
        VoicePicker.Tag = rows;

        var wanted = paid ? settings.ElevenLabsVoice : settings.SpeechVoice;
        var index = rows.FindIndex(r => r.Id == wanted);
        VoicePicker.SelectedIndex = index >= 0 ? index : 0;

        VoicePicker.IsEnabled = rows.Count > 0;
        VoiceTestButton.IsEnabled = rows.Count > 0;

        // An empty list is never self-explanatory. Whatever the reason, say it rather than
        // guessing at the likeliest one — that is how a wrong endpoint spent an afternoon
        // masquerading as an unsaved key.
        VoiceHint.Text = (paid, voices.Count) switch
        {
            (true, 0) => _speaker.VoiceListError
                         ?? "Save a key above to load the voices on your ElevenLabs account.",
            (true, _) => "Your ElevenLabs voices. Each one you hear here is billed like any "
                         + "other, so the sample is not free.",
            (false, 0) => "No voices are installed on this machine.",
            _ => "These are the voices Windows ships. None will be mistaken for a person — "
                 + "the ones marked older are the 2009 set, and worth avoiding.",
        };
    }

    /// <summary>"Microsoft Catherine" reads better in a list as "Catherine".</summary>
    private static string Shorten(string voice) =>
        voice.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) ? voice[10..] : voice;

    private void OnVoiceChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _speaker is null || VoicePicker.Tag is not List<VoiceChoice> rows) return;
        if (VoicePicker.SelectedIndex < 0 || VoicePicker.SelectedIndex >= rows.Count) return;

        var chosen = rows[VoicePicker.SelectedIndex];
        var settings = _read();

        _write(settings.VoiceProvider == VoiceProvider.ElevenLabs
            ? settings with { ElevenLabsVoice = chosen.Id }
            : settings with { SpeechVoice = chosen.Id });

        // Applied and demonstrated at once. Choosing a voice from a list of names without
        // hearing it is guessing, and the whole point of the row is what it sounds like.
        _speaker.PreferredVoice = chosen.Id;
        Speak();

        // The paid tier bills for the sample too, so the counter has just moved.
        if (settings.VoiceProvider == VoiceProvider.ElevenLabs) ShowElevenKeyState();
    }

    private void OnVoiceProviderChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || VoiceProviderPicker.SelectedIndex < 0) return;

        _write(_read() with { VoiceProvider = VoiceProviders[VoiceProviderPicker.SelectedIndex].Provider });
        Refresh();
    }

    // ---- the ElevenLabs key ----

    private void OnElevenKeyTyped(object sender, RoutedEventArgs e) =>
        SaveElevenKeyButton.IsEnabled = ElevenKeyBox.Password.Trim().Length > 0;

    private void OnSaveElevenKey(object sender, RoutedEventArgs e)
    {
        var key = ElevenKeyBox.Password.Trim();
        if (key.Length == 0 || _secrets is null) return;

        _secrets.Write(App.ElevenLabsKeyName, key);
        ElevenKeyBox.Clear();

        // The voice list is fetched with the key, so it could not be populated until now.
        Refresh();
    }

    private void OnForgetElevenKey(object sender, RoutedEventArgs e)
    {
        _secrets?.Delete(App.ElevenLabsKeyName);
        Refresh();
    }

    /// <summary>
    /// Whether a key is saved, and what has been spoken with it this month.
    /// </summary>
    /// <remarks>
    /// Last month leads, because that is the figure a tier is chosen on: this month is always
    /// partial, and on the second of the month it says almost nothing.
    /// </remarks>
    private void ShowElevenKeyState()
    {
        var saved = _secrets?.Describe(App.ElevenLabsKeyName);

        // The mistake worth catching before a network round trip: the dashboard shows a key
        // *ID* next to each key, and it is the more obvious thing to copy. The key itself is
        // shown once, at creation. Saying so here beats a 400 several clicks later.
        var looksLikeAnId = _secrets?.Read(App.ElevenLabsKeyName) is { Length: > 0 } key
                            && !key.StartsWith("sk_", StringComparison.Ordinal);

        ElevenKeyStatus.Text = (saved, looksLikeAnId) switch
        {
            (null, _) => "No key saved. Create one at elevenlabs.io, under your profile.",
            (_, true) => $"Saved ({saved}), but that looks like a key ID rather than a key — "
                         + "keys begin with “sk_” and are shown only when you create or rotate one.",
            _ => $"Key saved ({saved}). Encrypted for your Windows account.",
        };

        ForgetElevenKeyButton.Visibility = saved is null ? Visibility.Collapsed : Visibility.Visible;

        if (_usage is null)
        {
            VoiceUsageText.Text = string.Empty;
            return;
        }

        var thisMonth = _usage.ThisMonth;
        var lastMonth = _usage.LastMonth;

        VoiceUsageText.Text = lastMonth > 0
            ? $"Spoken this month: {thisMonth:N0} characters. Last full month: {lastMonth:N0}. "
              + "ElevenLabs sells a monthly character allowance rather than charging per use, "
              + "so last month's figure is the one to pick a tier against."
            : $"Spoken this month: {thisMonth:N0} characters. ElevenLabs sells a monthly "
              + "character allowance rather than charging per use — leave this a few weeks and "
              + "the number here will tell you which tier you actually need.";
    }

    // ---- calendar accounts ----

    /// <summary>One connected account, as the list shows it.</summary>
    /// <remarks>
    /// A small view model rather than binding to <see cref="ConnectedAccount"/> directly: the
    /// record is immutable, and the profile picker has to write a change back through settings
    /// rather than mutate what it was handed.
    /// </remarks>
    private sealed class CalendarRow(ConnectedAccount account)
    {
        /// <summary>Shared, so reading the options twice yields the same collection.</summary>
        private static readonly CalendarProfile[] Both =
            [CalendarProfile.Personal, CalendarProfile.Work];

        public ConnectedAccount Account { get; } = account;

        public string DisplayName => Account.DisplayName;

        public string Detail => Account.Source switch
        {
            CalendarSource.Microsoft => "Microsoft",
            CalendarSource.Google => "Google",
            _ => "Calendar link · read-only",
        };

        /// <summary>What the profile picker offers.</summary>
        /// <remarks>
        /// An instance property although it never varies: a binding path cannot reach a static
        /// member through the item's data context, so a static one would leave every picker
        /// empty — and silently, since a failed binding raises nothing.
        /// </remarks>
        public CalendarProfile[] Profiles => Both;

        /// <summary>Read only, and bound OneTime. The picker reports changes instead.</summary>
        public CalendarProfile Profile => Account.Profile;
    }

    private void PopulateCalendar(TeezySettings settings)
    {
        // Shown until an account from that provider is actually connected, not merely until an
        // id is saved. A wrong id fails at sign-in, and hiding the only field that can fix it
        // the moment it is first saved would leave editing settings.json as the only way out.
        //
        // Per provider, not per account. When this was "any account at all" a connected
        // Microsoft account hid the Google box too, which left no way to enter a Google client
        // id and so no way to ever enable its button.
        CalendarSetup.Visibility = Connected(settings, CalendarSource.Microsoft)
            ? Visibility.Collapsed
            : Visibility.Visible;

        MicrosoftClientIdBox.Text = settings.MicrosoftClientId ?? string.Empty;

        GoogleSetup.Visibility = Connected(settings, CalendarSource.Google)
            ? Visibility.Collapsed
            : Visibility.Visible;

        GoogleClientIdBox.Text = settings.GoogleClientId ?? string.Empty;

        ConnectGoogleButton.IsEnabled =
            _calendars is not null && !string.IsNullOrWhiteSpace(settings.GoogleClientId);

        ShowGoogleReady();

        CalendarAccountList.ItemsSource = settings.ConnectedAccounts
            .Select(a => new CalendarRow(a))
            .ToList();

        ConnectMicrosoftButton.IsEnabled =
            _calendars is not null && !string.IsNullOrWhiteSpace(settings.MicrosoftClientId);

        var anyClientId = !string.IsNullOrWhiteSpace(settings.MicrosoftClientId)
                          || !string.IsNullOrWhiteSpace(settings.GoogleClientId);

        // Both providers, not just Microsoft's. The same oversight as the setup boxes above,
        // and it would have told someone setting up Google alone to add an id they had.
        CalendarStatus.Text = (anyClientId, settings.ConnectedAccounts.Count) switch
        {
            (false, 0) => "Add an application id above to connect an account, or add a calendar link.",
            (_, 0) => "No accounts connected. Nothing about your diary leaves this machine "
                      + "until one is.",

            // Worth saying plainly, because it is the difference between this feature and the
            // integrations people are right to be wary of.
            _ => "Read-only. TeezyFlow can see what is in your diary and cannot change any of it.",
        };

        // Offered only once there is an account to read, since the switch would otherwise be a
        // promise about a mailbox that does not exist yet.
        MailDetail.Visibility = settings.ConnectedAccounts.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        ReadMailBox.IsChecked = settings.ReadMailEnabled;
        GmailAddressBox.Text = settings.GmailAddress ?? string.Empty;
        ShowGmailReady();

        // The switch alone cannot grant anything: Microsoft hands over what the sign-in asked
        // for, and an account connected before this was on was never asked about mail. Saying
        // so here is the difference between a working feature and a puzzling 403.
        MailHint.Text = settings.ReadMailEnabled
            ? "Accounts connected before you switched this on will need reconnecting — "
              + "Microsoft only grants what it was asked for at sign-in, and the token you "
              + "already have never mentioned mail. Disconnect and connect again, and the "
              + "sign-in page will ask about your mail as well as your diary."
            : string.Empty;
    }

    // ---- Gmail over IMAP ----

    private void OnGmailTyped(object sender, RoutedEventArgs e) => ShowGmailReady();

    private void ShowGmailReady()
    {
        var address = GmailAddressBox.Text.Trim();
        var typed = GmailPasswordBox.Password.Trim().Length > 0;
        var stored = _secrets?.Describe(App.GmailPasswordName);

        SaveGmailButton.IsEnabled = address.Length > 0 && (typed || stored is not null);

        ForgetGmailButton.Visibility = stored is null ? Visibility.Collapsed : Visibility.Visible;

        GmailStatus.Text = (address.Length > 0, stored) switch
        {
            (false, _) => "Not set up. Nothing from Gmail is read.",

            // The mistake this catches early: an app password is sixteen letters in four
            // groups, and an ordinary account password is simply refused by Google.
            (true, null) => "Paste the app password too — sixteen letters, shown once when you "
                            + "create it. Your normal Google password will not work.",

            _ => $"App password saved ({stored}). Encrypted for your Windows account.",
        };
    }

    private void OnSaveGmail(object sender, RoutedEventArgs e)
    {
        var address = GmailAddressBox.Text.Trim();
        if (address.Length == 0) return;

        if (GmailPasswordBox.Password.Trim() is { Length: > 0 } password)
        {
            // Google prints app passwords in four spaced groups, and they are pasted that way
            // constantly. The spaces are presentation; IMAP wants the sixteen letters.
            _secrets?.Write(App.GmailPasswordName, password.Replace(" ", ""));
            GmailPasswordBox.Clear();
        }

        _write(_read() with { GmailAddress = address });
        Refresh();
    }

    private void OnForgetGmail(object sender, RoutedEventArgs e)
    {
        _secrets?.Delete(App.GmailPasswordName);
        _write(_read() with { GmailAddress = null });
        Refresh();
    }

    private void OnReadMailToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _write(_read() with { ReadMailEnabled = ReadMailBox.IsChecked == true });
        Refresh();
    }

    /// <summary>The user moving an account between work and personal.</summary>
    /// <remarks>
    /// Guarded by <c>_loading</c> like every other picker on this page, and checked against
    /// what is stored. Populating a list raises SelectionChanged too, and saving that as though
    /// someone had chosen it silently rewrote a connected account to Work the moment it was
    /// first shown.
    /// </remarks>
    private void OnCalendarProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox picker) return;
        if (picker.DataContext is not CalendarRow row) return;
        if (picker.SelectedItem is not CalendarProfile chosen) return;
        if (chosen == row.Account.Profile) return;

        Reprofile(row.Account, chosen);
    }

    private static bool Connected(TeezySettings settings, CalendarSource source) =>
        settings.ConnectedAccounts.Any(a => a.Source == source);

    private void Reprofile(ConnectedAccount account, CalendarProfile profile)
    {
        var settings = _read();

        _write(settings with
        {
            ConnectedAccounts = [.. settings.ConnectedAccounts.Select(
                a => a.Id == account.Id ? a with { Profile = profile } : a)],
        });
    }

    private void OnMicrosoftClientIdTyped(object sender, RoutedEventArgs e) =>
        SaveMicrosoftClientIdButton.IsEnabled = MicrosoftClientIdBox.Text.Trim().Length > 0;

    private void OnSaveMicrosoftClientId(object sender, RoutedEventArgs e)
    {
        var id = MicrosoftClientIdBox.Text.Trim();
        if (id.Length == 0) return;

        _write(_read() with { MicrosoftClientId = id });
        Refresh();
    }

    private void OnGoogleClientTyped(object sender, RoutedEventArgs e) => ShowGoogleReady();

    /// <summary>Whether there is enough to sign in with, said before you try.</summary>
    /// <remarks>
    /// Google refuses the exchange outright without a secret, and the refusal arrives at the
    /// far end of a browser round trip reading "client_secret is missing" — which is true, and
    /// useless, because by then the page that could fix it is behind you. Saving an id with no
    /// secret is simply not allowed.
    /// </remarks>
    private void ShowGoogleReady()
    {
        var id = GoogleClientIdBox.Text.Trim();
        var typed = GoogleSecretBox.Password.Trim().Length > 0;
        var stored = _secrets?.Describe(App.GoogleSecretName) is not null;

        SaveGoogleClientButton.IsEnabled = id.Length > 0 && (typed || stored);

        GoogleSecretHint.Text = (id.Length > 0, typed || stored) switch
        {
            (true, false) => "Google refuses to sign in without the secret, so TeezyFlow won’t "
                             + "save an ID on its own. It is shown next to the client ID in "
                             + "the Cloud console.",
            (_, true) when stored && !typed => "Secret saved, encrypted for your Windows "
                                               + "account. Leave this blank to keep it.",
            _ => string.Empty,
        };
    }

    private void OnSaveGoogleClient(object sender, RoutedEventArgs e)
    {
        var id = GoogleClientIdBox.Text.Trim();
        if (id.Length == 0) return;

        // Blank on a second save means "keep the one you have", which is why this is not a
        // hard requirement here — ShowGoogleReady is what stops a first save without one.
        if (GoogleSecretBox.Password.Trim() is { Length: > 0 } secret)
        {
            _secrets?.Write(App.GoogleSecretName, secret);
            GoogleSecretBox.Clear();
        }

        _write(_read() with { GoogleClientId = id });
        Refresh();
    }

    // async void because a WPF Click handler cannot be anything else. Both delegate straight
    // to a method that handles its own failures, so nothing escapes into the dispatcher.
    private async void OnConnectMicrosoft(object sender, RoutedEventArgs e) =>
        await ConnectAsync(ConnectMicrosoftButton, "Microsoft", () =>
            _calendars!.ConnectMicrosoftAsync(CalendarProfile.Personal));

    private async void OnConnectGoogle(object sender, RoutedEventArgs e) =>
        await ConnectAsync(ConnectGoogleButton, "Google", () =>
            _calendars!.ConnectGoogleAsync(CalendarProfile.Personal));

    /// <summary>Runs one sign-in, whoever it is with.</summary>
    /// <remarks>
    /// Shared because the interesting parts — the five-minute wait, the failure wording, the
    /// button that must come back enabled — are identical, and two copies would drift. Returns
    /// a Task rather than being async void so that nothing can escape into the dispatcher and
    /// take the process with it.
    /// </remarks>
    private async Task ConnectAsync(
        Button button, string provider, Func<Task<ConnectedAccount>> connect)
    {
        if (_calendars is null) return;

        button.IsEnabled = false;
        CalendarWarning.Visibility = Visibility.Collapsed;
        CalendarStatus.Text = "Waiting for you to sign in, in your browser…";

        try
        {
            // Personal to begin with. Which side of life an account belongs to is a judgement
            // only the user can make, and the picker on the row is where they make it —
            // guessing from the address would be wrong often enough to be annoying.
            var connected = await connect();

            var settings = _read();
            _write(settings with
            {
                ConnectedAccounts = [.. settings.ConnectedAccounts, connected],
            });

            Refresh();
        }
        catch (OAuthException failure)
        {
            Warn(failure.Message);
        }
        catch (Exception failure) when (failure is HttpRequestException or OperationCanceledException)
        {
            // OperationCanceledException rather than TaskCanceledException: the latter is the
            // subclass, so a plain cancellation — which is how running out of patience arrives
            // — would sail past a catch naming only the subclass.
            Warn($"Couldn’t reach {provider} to finish signing in.");
        }
        finally
        {
            button.IsEnabled = true;
        }

        void Warn(string why)
        {
            CalendarWarningText.Text = why;
            CalendarWarning.Visibility = Visibility.Visible;
            CalendarStatus.Text = string.Empty;
        }
    }

    private void OnCalendarLinkTyped(object sender, TextChangedEventArgs e) =>
        AddCalendarLinkButton.IsEnabled = _calendars is not null && CalendarLinkBox.Text.Trim().Length > 0;

    /// <summary>Checks and saves a published calendar link as a new account.</summary>
    private async void OnAddCalendarLink(object sender, RoutedEventArgs e)
    {
        var link = CalendarLinkBox.Text.Trim();
        if (_calendars is null || link.Length == 0) return;

        AddCalendarLinkButton.IsEnabled = false;
        CalendarWarning.Visibility = Visibility.Collapsed;
        CalendarStatus.Text = "Checking the link…";

        try
        {
            // Work to begin with: a published link is nearly always the way in to a calendar that
            // could not be signed in to, and that is a work one. The picker on the row changes it.
            var added = await _calendars.ConnectLinkAsync(link, "Work calendar", CalendarProfile.Work);

            var settings = _read();
            _write(settings with { ConnectedAccounts = [.. settings.ConnectedAccounts, added] });

            CalendarLinkBox.Clear();
            Refresh();
        }
        catch (CalendarUnavailableException problem)
        {
            CalendarWarningText.Text = problem.Message;
            CalendarWarning.Visibility = Visibility.Visible;
            CalendarStatus.Text = string.Empty;
        }
        finally
        {
            AddCalendarLinkButton.IsEnabled = CalendarLinkBox.Text.Trim().Length > 0;
        }
    }

    private void OnDisconnectCalendar(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CalendarRow row) return;

        // Tokens first: an interruption between the two leaves an orphaned secret rather than
        // an account that has vanished from the list but can still be read.
        _calendars?.Disconnect(row.Account);

        var settings = _read();
        _write(settings with
        {
            ConnectedAccounts = [.. settings.ConnectedAccounts.Where(a => a.Id != row.Account.Id)],
        });

        Refresh();
    }

    private void OnHearVoice(object sender, RoutedEventArgs e) => Speak();

    private void Speak() =>
        _speaker?.SpeakAsync("TeezyFlow will read your answers in this voice.");

    private void OnAssistantModelChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || AssistantModelPicker.SelectedIndex < 0) return;

        var chosen = AssistantModels[AssistantModelPicker.SelectedIndex];
        _write(_read() with { AssistantModel = chosen.Id });
        AssistantModelCost.Text = chosen.Cost;
    }

    private void ShowAssistantState(TeezySettings settings)
    {
        ShowAssistantLlm(settings);
        PopulateVoices(settings);

        var assistant = settings.AssistantHotkey;

        AssistantHint.Text = assistant.IsEmpty
            ? "Off. Pick a combination to switch it on."
            : $"Hold {assistant.Display} and say what you want.";

        // The overlap that cannot be designed away: holding a combination that contains the
        // dictation one satisfies it on the way, so dictation starts for a few milliseconds
        // first. Steering people away from it beats taxing every dictation with a debounce.
        var overlaps = !assistant.IsEmpty
                       && (assistant.Contains(settings.Hotkey) || settings.Hotkey.Contains(assistant));

        AssistantWarning.Visibility = overlaps ? Visibility.Visible : Visibility.Collapsed;
        if (overlaps)
        {
            AssistantWarningText.Text =
                $"{assistant.Display} and your dictation key {settings.Hotkey.Display} share keys, "
                + "so holding one briefly starts the other — you will hear the dictation tone "
                + "first. Combinations that do not contain one another avoid it.";
        }
    }

    private void OnAssistantPreset(object sender, RoutedEventArgs e)
    {
        if (_loading || AssistantPicker.Tag is not List<Hotkey> options) return;
        if (AssistantPicker.SelectedIndex < 0 || AssistantPicker.SelectedIndex >= options.Count) return;

        ApplyAssistantHotkey(options[AssistantPicker.SelectedIndex]);
    }

    private void OnRecordAssistantHotkey(object sender, RoutedEventArgs e)
    {
        if (_capture is null) return;

        if (_capture.IsCapturing)
        {
            _capture.CancelCapture();
            EndAssistantRecording();
            return;
        }

        AssistantRecordButton.Content = "Cancel";
        AssistantHint.Text = "Hold the keys you want, then let go.";
        AssistantPicker.IsEnabled = false;

        _capture.BeginCapture(hotkey => Dispatcher.Invoke(() =>
        {
            EndAssistantRecording();
            ApplyAssistantHotkey(hotkey);
        }));
    }

    private void EndAssistantRecording()
    {
        AssistantRecordButton.Content = "Record my own";
        AssistantPicker.IsEnabled = true;
    }

    private void ApplyAssistantHotkey(Hotkey hotkey)
    {
        var settings = _read();
        if (hotkey == settings.AssistantHotkey)
        {
            ShowAssistantState(settings);
            return;
        }

        _write(settings with { AssistantHotkey = hotkey });
        Refresh();
    }

    // ---- Microphone ----

    /// <summary>One row of the microphone picker.</summary>
    /// <param name="Id">Null for the "whatever Windows chooses" row.</param>
    /// <param name="Missing">Chosen previously, not present now.</param>
    private sealed record MicChoice(string? Id, string? Name, string Label, bool Missing = false);

    private void PopulateMicrophones(TeezySettings settings)
    {
        IReadOnlyList<AudioDevice> devices = [];
        if (_microphone is not null)
        {
            // Disposed straight away: this one exists to ask what is plugged in, not to
            // record. Enumerating does not open a device.
            using var probe = _microphone();
            devices = probe.Devices();
        }

        _micDeviceCount = devices.Count;

        var rows = new List<MicChoice>
        {
            // Leading, and the default, because it is the right answer for most people: it
            // re-decides when a headset is plugged in, which a pinned device cannot.
            new(null, null, devices.FirstOrDefault(d => d.IsSystemDefault) is { } d
                ? $"Windows default — {d.Name}"
                : "Windows default"),
        };

        rows.AddRange(devices.Select(device => new MicChoice(device.Id, device.Name, device.Name)));

        // A chosen microphone that is unplugged today keeps its row rather than vanishing.
        // Dropping it would silently reset the setting, and the next time the headset came
        // back it would not be used.
        if (settings.InputDeviceId is { Length: > 0 } chosen && rows.All(r => r.Id != chosen))
        {
            rows.Add(new MicChoice(
                chosen,
                settings.InputDeviceName,
                $"{settings.InputDeviceName ?? "Chosen microphone"} — not connected",
                Missing: true));
        }

        MicPicker.Items.Clear();
        foreach (var row in rows) MicPicker.Items.Add(row.Label);
        MicPicker.Tag = rows;

        var index = rows.FindIndex(r => r.Id == settings.InputDeviceId);
        MicPicker.SelectedIndex = index >= 0 ? index : 0;
        MicPicker.IsEnabled = _microphone is not null && devices.Count > 0;

        ShowMicrophoneState(rows.ElementAtOrDefault(MicPicker.SelectedIndex));
    }

    private void ShowMicrophoneState(MicChoice? chosen)
    {
        if (_micDeviceCount == 0)
        {
            MicInUse.Text = "No microphone found. Plug one in, then reopen Settings.";
            MicWarning.Visibility = Visibility.Collapsed;
            return;
        }

        MicInUse.Text = chosen?.Id is null
            ? "Follows Windows, so plugging in a headset switches to it automatically."
            : "TeezyFlow always records from this device, whatever Windows is set to.";

        MicWarning.Visibility = chosen?.Missing == true ? Visibility.Visible : Visibility.Collapsed;
        if (chosen?.Missing == true)
        {
            MicWarningText.Text =
                $"{chosen.Name ?? "That microphone"} is not connected. TeezyFlow is recording from the "
                + "Windows default until it comes back — it stays selected, so plugging it in is "
                + "all it takes.";
        }
    }

    private void OnMicrophoneChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || MicPicker.Tag is not List<MicChoice> rows) return;
        if (MicPicker.SelectedIndex < 0 || MicPicker.SelectedIndex >= rows.Count) return;

        var chosen = rows[MicPicker.SelectedIndex];

        // The name rides along only so an absent device can be named later. The id is what
        // selects.
        _write(_read() with { InputDeviceId = chosen.Id, InputDeviceName = chosen.Name });

        ShowMicrophoneState(chosen);

        // A test in progress is about a device the user has just stopped caring about.
        if (_preview is not null) StartPreview();
    }

    /// <summary>How long a forgotten test holds the microphone before closing it.</summary>
    private static readonly TimeSpan PreviewLimit = TimeSpan.FromSeconds(30);

    private void OnTestMicrophone(object sender, RoutedEventArgs e)
    {
        if (_preview is not null) StopPreview();
        else StartPreview();
    }

    private void StartPreview()
    {
        if (_microphone is null) return;

        StopPreview();

        var capture = _microphone();
        capture.PreferredDeviceId = _read().InputDeviceId;
        capture.LevelChanged += OnPreviewLevel;

        try
        {
            capture.Start();
        }
        catch (AudioCaptureException ex)
        {
            capture.LevelChanged -= OnPreviewLevel;
            capture.Dispose();
            MicTestStatus.Text = ex.Message;
            return;
        }

        _preview = capture;
        _previewStarted = DateTime.UtcNow;
        _previewLevel = 0;
        _previewPeak = 0;

        MicTestButton.Content = "Stop test";
        MicTestStatus.Text = "Opening the microphone…";

        // Separate from the level events on purpose. The verdict depends on how long we have
        // been listening, and a silent device raises no events at all to hang it off — which
        // is precisely the case that most needs something said about it.
        _previewTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _previewTimer.Tick += OnPreviewTick;
        _previewTimer.Start();
    }

    private void StopPreview()
    {
        if (_previewTimer is not null)
        {
            _previewTimer.Stop();
            _previewTimer.Tick -= OnPreviewTick;
            _previewTimer = null;
        }

        if (_preview is not null)
        {
            _preview.LevelChanged -= OnPreviewLevel;
            try { _preview.Stop(); } catch (AudioCaptureException) { /* already down */ }
            _preview.Dispose();
            _preview = null;
        }

        MicTestButton.Content = "Start test";
        MicLevelFill.Width = 0;
    }

    /// <summary>Arrives on the capture thread.</summary>
    private void OnPreviewLevel(float level) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_preview is null) return;

            // Snap up, fall slowly — the same asymmetry the HUD meter uses, and for the same
            // reason: a syllable must register at once, but the bar must settle between words
            // rather than strobe.
            var rate = level > _previewLevel ? 0.6f : 0.15f;
            _previewLevel += (level - _previewLevel) * rate;
            _previewPeak = Math.Max(_previewPeak, level);

            MicLevelFill.Width = MicLevelTrack.ActualWidth * _previewLevel;
        });

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        if (_preview is null) return;

        var elapsed = DateTime.UtcNow - _previewStarted;
        if (elapsed > PreviewLimit)
        {
            StopPreview();
            MicTestStatus.Text = "Test stopped. Start it again whenever you need it.";
            return;
        }

        MicTestStatus.Text = Verdict(elapsed);
    }

    /// <summary>
    /// What the meter means, said in words.
    /// </summary>
    /// <remarks>
    /// The distinction that matters is between "quiet" and "nothing". Both draw an empty bar,
    /// but one is solved by speaking up and the other cannot be solved by speaking at all —
    /// when Windows blocks desktop apps from the microphone, WASAPI hands back digital zeroes
    /// forever and nothing anywhere throws. Someone left to interpret a flat bar will always
    /// try talking louder first.
    /// </remarks>
    private string Verdict(TimeSpan elapsed)
    {
        if (_preview?.SawSignal != true)
        {
            return elapsed < TimeSpan.FromSeconds(2)
                ? "Listening — say something."
                : "Not hearing anything at all. Check Settings › Privacy › Microphone in "
                  + "Windows, and that “Let desktop apps access your microphone” is on.";
        }

        var device = _preview.DeviceName ?? "this microphone";

        return _previewPeak switch
        {
            < 0.2f => $"{device} is picking up sound, but very faintly. Move closer, raise its "
                      + "level in Windows, or try another device.",
            < 0.45f => $"{device} is a little quiet. Usable, but closer or louder would "
                       + "transcribe more accurately.",
            _ => $"{device} sounds good — that is a healthy level for dictation.",
        };
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
            ExplainAutostart($"Windows would not let TeezyFlow change the startup entry — {ex.Message}");
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
                : "TeezyFlow could not remove its startup entry.");
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
            Margin = new Thickness(0, 0, 8, 0),
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
            Style = (Style)FindResource("Field"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
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

    /// <summary>
    /// Benchmarks thread counts on this machine and keeps the fastest.
    /// </summary>
    /// <remarks>
    /// The result deliberately reports what it <i>cannot</i> fix as well as what it did. Thread
    /// count is the only part of a slow machine that a setting can address; a throttled CPU, a
    /// corporate proxy in front of the Claude tier and an endpoint-security product sitting on
    /// the microphone all look identical from in here, and a tuner that quietly changed a
    /// number and said nothing would leave someone none the wiser about any of them.
    /// </remarks>
    private async void OnCheckMachine(object sender, RoutedEventArgs e)
    {
        if (_transcriber is null || !_transcriber.IsLoaded)
        {
            CheckResult.Text = "The speech model is not loaded yet.";
            CheckResult.Visibility = Visibility.Visible;
            return;
        }

        CheckMachineButton.IsEnabled = false;
        CheckResult.Visibility = Visibility.Collapsed;
        var progress = new Progress<string>(text => CheckProgress.Text = text);

        try
        {
            var result = await MachineCheck.RunAsync(_transcriber, progress);

            // Persist it, or the winner is lost the next time the recogniser is built.
            _write(_read() with { NumThreads = result.Best });
            ThreadPicker.SelectedItem = result.Best;

            CheckResult.Text = Describe(result);
            CheckResult.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is TranscriberException or InvalidOperationException)
        {
            CheckResult.Text = $"Could not finish the check — {ex.Message}";
            CheckResult.Visibility = Visibility.Visible;
        }
        finally
        {
            CheckProgress.Text = string.Empty;
            CheckMachineButton.IsEnabled = true;
            ShowModelState();
        }
    }

    private static string Describe(MachineCheckResult result)
    {
        var timings = string.Join("  ·  ",
            result.Results.Select(r => $"{r.Threads}: {r.Milliseconds:0} ms"));

        var verdict = result.IsWorthApplying
            ? $"Switched to {result.Best} threads — about {result.GainPercent:0}% faster than {result.Previous}."
            : result.Best == result.Previous
                ? $"{result.Previous} threads was already the best of these."
                : $"{result.Best} threads won, but only by {result.GainPercent:0}% — close enough to be noise.";

        return $"{verdict}\n{timings}\n\n"
               + "These compare thread counts against each other on synthesised audio; the "
               + "milliseconds are a floor, not your real speed. Insights reports what actual "
               + "dictations cost. Nothing here can help with a throttled CPU, a slow network "
               + "for the Claude tier, or security software in the way — if the numbers above "
               + "are close and TeezyFlow still feels slow, the cause is one of those.";
    }

    private void ShowModelState()
    {
        var loaded = _transcriber?.IsLoaded == true;

        ModelDot.Fill = loaded ? Brand.Accent : Brand.Faint;
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

    /// <summary>Releases what this page was holding when it is navigated away from.</summary>
    /// <remarks>
    /// Two things, for two reasons. A hotkey capture left active would swallow the next real
    /// press, because the source routes key events to the capture rather than to dictation
    /// while one is running. A level test left running would hold the microphone open behind
    /// a page nobody is looking at — visible to the user as the recording indicator in the
    /// system tray, which is not a thing a dictation app should leave lit.
    /// </remarks>
    public void Leaving()
    {
        _capture?.CancelCapture();
        EndRecording();
        EndAssistantRecording();
        StopPreview();
        MicTestStatus.Text = string.Empty;
    }
}
