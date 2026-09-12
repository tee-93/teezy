using Teezy.Assistant;
using Teezy.Calendar;
using Teezy.Core.Calendar;
using Teezy.Cleanup;
using Teezy.Core.Formatting;
using Teezy.Core.History;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Teezy.Core;
using Teezy.Core.Abstractions;
using Teezy.Core.Dictionary;
using Teezy.Core.Hotkeys;
using Teezy.Core.Voice;
using Teezy.Platform.Windows;
using Teezy.Speech;
using Forms = System.Windows.Forms;

namespace Teezy.App;

public partial class App : Application
{
    /// <summary>Name the API key is filed under in the encrypted secret store.</summary>
    internal const string ApiKeyName = "anthropic-api-key";

    /// <summary>Name the ElevenLabs key is filed under. Separate account, separate secret.</summary>
    internal const string ElevenLabsKeyName = "elevenlabs-api-key";

    private VoiceSession? _session;
    private DictationController? _controller;
    private AssistantController? _assistant;
    private AssistantWindow? _assistantHud;
    private ClaudeAssistant? _claudeAssistant;
    private ClaudeCalendarNarrator? _narrator;
    private CalendarAccounts? _calendars;
    private SwitchingSpeaker? _speaker;
    private VoiceUsage? _voiceUsage;
    private ParakeetTranscriber? _transcriber;
    private WindowsAutostart? _autostart;
    private WindowsHotkeySource? _hotkeySource;
    private HudWindow? _hud;
    private Forms.NotifyIcon? _tray;
    private DictionaryStore? _dictionary;
    private TeezySettings _settings = new();
    private bool _modelReady;
    private FileSystemWatcher? _dictWatcher;
    private SingleInstance? _instance;
    private Forms.ToolStripMenuItem? _downloadItem;
    private HistoryStore? _history;
    private ISecretStore? _secrets;
    private ClaudeFormatter? _claude;
    private MainWindow? _main;
    private WindowsAudioCapture? _audio;

    /// <summary>Keeps the "your microphone is missing" notice to once per run.</summary>
    private bool _warnedAboutMicrophone;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        CrashLog.Install(this);

        // Before anything else: two instances would install two hooks on the same key and
        // type every utterance twice.
        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            MessageBox.Show(
                """
                Teezy is already running.

                Look for the speech bubble in the system tray — click the ^ arrow
                next to the clock if you cannot see it.
                """,
                "Teezy", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = TeezySettings.Load();
        _dictionary = new DictionaryStore(DictionaryStore.DefaultPath);
        EnsureDictionaryFileExists();
        WatchDictionaryFile();

        _hud = new HudWindow();
        _assistantHud = new AssistantWindow();
        BuildTray();

        _transcriber = new ParakeetTranscriber(
            _settings.ModelPath,
            new SpeechOptions
            {
                Threads = _settings.NumThreads,
                Decoding = _settings.Decoding,
                BeamSize = _settings.BeamSize,
                HotwordScore = (float)_settings.HotwordScore,
            },

            // Read when the recogniser is built. ReloadDictionary rebuilds it after an edit,
            // so a hint added at lunchtime works that afternoon rather than after a restart.
            hotwords: () => _dictionary?.Hotwords());

        _history = new HistoryStore();
        _history.Compact();

        // Re-point the startup entry if the exe has moved since it was registered. Without
        // this, republishing to a new folder leaves a Run value aimed at a file that no
        // longer exists, and nothing reports a startup entry that failed to resolve.
        _autostart = new WindowsAutostart();
        _autostart.RefreshPathIfRegistered();

        // Held as a field as well as handed to the controller: the settings picker needs it
        // to record a combination.
        _hotkeySource = new WindowsHotkeySource();

        _secrets = new WindowsSecretStore();

        // Composed so the offline rules always run and their output is the floor: the LLM is
        // asked to improve an already-clean string, and every failure path returns it.
        _claude = new ClaudeFormatter(
            new RuleBasedFormatter(),
            () => _settings.LlmCleanupEnabled ? _secrets.Read(ApiKeyName) : null,
            () => _settings.LlmModel,
            TimeSpan.FromSeconds(Math.Clamp(_settings.LlmTimeoutSeconds, 2, 30)),
            context => _settings.StyleFor(context.App));

        // Held as a field too: Settings reads the device it actually opened, which is not
        // always the one that was chosen.
        _audio = new WindowsAudioCapture { PreferredDeviceId = _settings.InputDeviceId };

        // One session, shared. There is one hook and one microphone, so every voice mode
        // registers on the same session rather than each owning its own.
        _session = new VoiceSession(_hotkeySource, _audio, _transcriber, () => _settings);

        _controller = new DictationController(
            _session,
            new WindowsTextInjector(),
            _dictionary,
            () => _settings,
            new WindowsForegroundApp(),
            () => _settings.LlmCleanupEnabled ? _claude! : new RuleBasedFormatter());

        // Reuses the cleanup tier's key: it is the same Anthropic account, and asking someone
        // to paste the same key twice would be a small insult.
        _claudeAssistant = new ClaudeAssistant(
            () => _settings.AssistantLlmEnabled ? _secrets.Read(ApiKeyName) : null,
            () => _settings.AssistantModel,
            TimeSpan.FromSeconds(Math.Clamp(_settings.AssistantTimeoutSeconds, 2, 30)));

        _calendars = new CalendarAccounts(
            new TokenStore(_secrets), () => _settings.MicrosoftClientId);

        // Deliberately a second Claude client rather than a flag on the first. This one is
        // given no tools because it is handed meeting subjects other people wrote; that
        // property survives only as long as the two stay separate objects.
        _narrator = new ClaudeCalendarNarrator(
            () => _settings.AssistantLlmEnabled ? _secrets.Read(ApiKeyName) : null,
            () => _settings.AssistantModel,
            TimeSpan.FromSeconds(Math.Clamp(_settings.AssistantTimeoutSeconds, 2, 30)));

        _assistant = new AssistantController(
            _session,
            new WindowsCommandRunner(),
            _claudeAssistant,

            // Asked per question rather than built once, so connecting an account in Settings
            // works immediately instead of at the next launch.
            new CombinedCalendar(() => _calendars.Open(_settings.CalendarAccounts)),
            _narrator);

        _voiceUsage = new VoiceUsage();

        // Both tiers exist; which one answers is a setting, read per utterance. The paid one
        // falls back to the local one rather than to silence — a user left unsure whether the
        // assistant heard them is the one thing the voice exists to prevent.
        _speaker = new SwitchingSpeaker(
            () => _settings,
            new WindowsSpeaker(),
            new ElevenLabsSpeaker(
                () => _secrets.Read(ElevenLabsKeyName),
                () => _settings.ElevenLabsModel,
                _voiceUsage));

        // Every one of these fires on a background thread. WPF objects may only be touched
        // from the UI thread, so each hops the dispatcher rather than assuming.
        _session.StateChanged += (action, state) => Dispatch(() => OnStateChanged(action, state));
        _session.LevelChanged += l => Dispatch(() => OnLevel(l));
        _session.Failed += (action, m) => Dispatch(() => OnFailed(action, m));
        _controller.Completed += OnCompleted;
        _assistant.Finished += o => Dispatch(() => OnAssistantFinished(o));
        _assistant.Thinking += () => Dispatch(() => _assistantHud!.ShowWorking());

        // The hook must be installed from a thread with a message pump; OnStartup is on the
        // UI thread, which has one. From a pool thread the callback silently never fires.
        if (!_session.Start())
        {
            Notify("Teezy could not install its keyboard hook.", Forms.ToolTipIcon.Error);
        }

        // Open the window unless Windows started us at sign-in.
        //
        // Starting silently in the tray is right for the sign-in launch and wrong for every
        // other one: someone who has just installed Teezy, or just double-clicked it, gets no
        // acknowledgement that anything happened and no way to find out short of knowing to
        // look behind the ^ in the notification area.
        //
        // Deferred past a model download when there is one. On a fresh machine the download
        // window is the whole story at that moment, and two windows at once is not an
        // introduction.
        var greet = !e.Args.Contains(WindowsAutostart.StartupFlag, StringComparer.OrdinalIgnoreCase);
        var modelPresent = ModelPaths.Resolve(_settings.ModelPath) is not null;

        if (greet && modelPresent) ShowMainWindow();

        await LoadModelAsync().ConfigureAwait(false);

        // Back off the UI thread after the await, so this hops the dispatcher like everything
        // else that touches a window.
        if (greet && !modelPresent) Dispatch(() => ShowMainWindow());
    }

    private async Task LoadModelAsync()
    {
        // First run on a new machine: fetch the model before trying to load it. Checking for
        // the files here rather than catching the load failure keeps the two concerns apart —
        // "not installed yet" is setup, "installed but broken" is an error.
        if (!EnsureModelPresent()) return;

        SetTrayState("Loading the speech model…", ready: false);
        try
        {
            var sw = Stopwatch.StartNew();
            await _transcriber!.LoadAsync().ConfigureAwait(false);
            _modelReady = true;
            Dispatch(() => SetTrayState(
                $"Ready — hold {_settings.Hotkey.Display} to dictate", ready: true));
            Debug.WriteLine($"model loaded in {sw.ElapsedMilliseconds} ms");
        }
        catch (TranscriberException ex)
        {
            Dispatch(() =>
            {
                SetTrayState("Speech model not found", ready: false);
                MessageBox.Show(ex.Message, "Teezy — model problem",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            });
        }
    }

    /// <summary>Downloads the model if it is not already on disk.</summary>
    /// <returns><c>false</c> if the user cancelled, or the download failed.</returns>
    /// <remarks>
    /// Declining is not fatal. The app keeps running with the tray icon showing that it is
    /// not ready, and "Download speech model…" stays in the menu — better than quitting on
    /// someone who just wanted to postpone a 661 MB transfer.
    /// </remarks>
    private bool EnsureModelPresent()
    {
        if (ModelPaths.Resolve(_settings.ModelPath) is not null) return true;

        var directory = _settings.ModelPath ?? ModelPaths.DefaultDirectory;
        var window = new ModelDownloadWindow(directory);
        window.ShowDialog();

        if (window.Succeeded) return true;

        Dispatch(() => SetTrayState("Speech model not installed", ready: false));
        return false;
    }

    private void OnCompleted(DictationCompleted result)
    {
        Debug.WriteLine(
            $"{result.AudioDuration.TotalSeconds:F1}s audio -> "
            + $"{result.ProcessingTime.TotalMilliseconds:F0} ms, "
            + $"{result.Injection}, {result.Corrections.Count} correction(s)");

        // Recorded even when injection failed — that is precisely when the user needs the
        // text back, because it did not land anywhere they can reach it.
        _history?.Add(new HistoryEntry
        {
            Id = Guid.NewGuid().ToString("N"),
            At = DateTimeOffset.Now,
            Text = result.Text,
            AudioSeconds = result.AudioDuration.TotalSeconds,
            ProcessingMs = result.ProcessingTime.TotalMilliseconds,
            App = result.App,
            Corrections = result.Corrections.Count,
            Tokens = result.Tokens,
            Model = result.Model,
            TranscribeMs = result.Stages?.Transcribe.TotalMilliseconds,
            CleanupMs = result.Stages?.Cleanup.TotalMilliseconds,
            InjectMs = result.Stages?.Inject.TotalMilliseconds,
        });

        // A window that is open should show the utterance that just landed.
        Dispatch(() => { if (_main?.IsVisible == true) _main.RefreshAfterDictation(); });

        if (result.Injection == InjectionResult.Failed)
        {
            Dispatch(() => Notify(
                "Couldn't type into that window. Elevated apps need Teezy to be elevated too.",
                Forms.ToolTipIcon.Warning));
        }
    }

    private void OnFailed(HotkeyAction action, string message)
    {
        if (action == HotkeyAction.Assistant) _assistantHud!.ShowError(message);
        else _hud!.ShowState(DictationState.Error, message);

        Notify(message, Forms.ToolTipIcon.Error);
    }

    /// <summary>The meter belongs to whichever pill is up.</summary>
    private void OnLevel(float level)
    {
        _hud!.SetLevel(level);
        _assistantHud!.SetLevel(level);
    }

    private void OnAssistantFinished(AssistantOutcome outcome)
    {
        switch (outcome.Result)
        {
            case AssistantResult.Did:
                _assistantHud!.ShowDone(outcome.Message);
                break;

            case AssistantResult.Answered:
                _assistantHud!.ShowAnswer(outcome.Message);
                if (_settings.SpeakAnswers) _ = _speaker?.SpeakAsync(outcome.Message);
                break;

            case AssistantResult.Failed:
                // Understood but not done, so the message is the useful part rather than the
                // transcript — it already knows it heard correctly.
                _assistantHud!.ShowError(outcome.Message);
                break;

            default:
                _assistantHud!.ShowUnknown(outcome.Heard);
                break;
        }

        // Answering costs money. Recorded the same way cleanup is, so Insights can show what
        // the assistant tier actually spends rather than leaving it to be discovered on a bill.
        if (outcome.Result == AssistantResult.Answered && _claudeAssistant?.LastTokens is { } tokens)
        {
            _history?.Add(new HistoryEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                At = DateTimeOffset.Now,
                Text = outcome.Message,
                Tokens = tokens,
                Model = _claudeAssistant.LastModel,
            });
        }
    }

    private void Dispatch(Action action) => Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);

    // ---- Tray ----

    private void BuildTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIcons.Ready,
            Visible = true,
            Text = "Teezy — starting…",
        };

        var menu = new Forms.ContextMenuStrip();
        // Bold marks it as the default action, matching what a double-click does.
        var open = new Forms.ToolStripMenuItem("Open Teezy", null, (_, _) => ShowMainWindow())
        {
            Font = new System.Drawing.Font(
                System.Drawing.SystemFonts.MenuFont!, System.Drawing.FontStyle.Bold),
        };
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Settings…", null, (_, _) => ShowMainWindow(Page.Settings));
        menu.Items.Add("Dictionary…", null, (_, _) => ShowMainWindow(Page.Dictionary));

        // Only meaningful when setup was cancelled or failed, so it hides itself once the
        // model is loaded rather than sitting in the menu as a permanent puzzle.
        _downloadItem = new Forms.ToolStripMenuItem("Download speech model…", null,
            async (_, _) => await LoadModelAsync());
        menu.Items.Add(_downloadItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Teezy", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    /// <summary>Opens the history and insights window, or brings it back to the front.</summary>
    /// <remarks>
    /// Built once and hidden on close rather than recreated: the window holds page state and
    /// a scroll position, and rebuilding it every time would lose both.
    /// </remarks>
    private void ShowMainWindow(Page page = Page.Home)
    {
        _main ??= new MainWindow(
            _history!,
            _dictionary!,
            () => _settings,
            updated => ApplySettings(updated),
            _transcriber,
            _autostart,
            _hotkeySource,
            _secrets,
            _claude,

            // A fresh capture per preview rather than the controller's own. Settings opens
            // the microphone to show a level meter, and borrowing the instance dictation
            // depends on would let a forgotten test leave it in a state a hotkey press then
            // inherits.
            microphone: () => new WindowsAudioCapture(),
            speaker: _speaker,
            usage: _voiceUsage,
            calendars: _calendars);

        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.ShowPage(page);
        _main.RefreshCurrentPage();
    }

    /// <summary>Applies and persists a settings change from any window.</summary>
    private void ApplySettings(TeezySettings updated)
    {
        var keyChanged = updated.Hotkey != _settings.Hotkey
                         || updated.AssistantHotkey != _settings.AssistantHotkey;
        var micChanged = updated.InputDeviceId != _settings.InputDeviceId;
        _settings = updated;
        _settings.Save();
        if (keyChanged) _session?.ReloadHotkeys();

        // A newly chosen microphone deserves to be reported on its own merits, even if the
        // previous one had already been warned about.
        if (micChanged) _warnedAboutMicrophone = false;

        // The picker sets this directly for an immediate preview; this is for the other ways
        // settings can change, and for a voice restored at startup.
        // Per provider: the two name their voices in completely different namespaces, and
        // handing a SAPI voice name to ElevenLabs would silently leave it with no voice.
        if (_speaker is not null)
        {
            _speaker.PreferredVoice = _settings.VoiceProvider == VoiceProvider.ElevenLabs
                ? _settings.ElevenLabsVoice
                : _settings.SpeechVoice;
        }

        SetTrayState($"Ready — hold {_settings.Hotkey.Display} to dictate", _modelReady);
    }

    private void SetTrayState(string text, bool ready)
    {
        if (_tray is null) return;
        // NotifyIcon.Text is capped at 63 characters and throws above it.
        _tray.Text = text.Length > 63 ? text[..63] : text;
        if (_downloadItem is not null) _downloadItem.Visible = !ready;
        _tray.Icon = ready ? TrayIcons.Ready : TrayIcons.Busy;
    }

    private void Notify(string message, Forms.ToolTipIcon icon) =>
        _tray?.ShowBalloonTip(4000, "Teezy", message, icon);


    private void EnsureDictionaryFileExists()
    {
        var path = DictionaryStore.DefaultPath;
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, DictionaryStore.SampleFile);
    }



    protected override void OnExit(ExitEventArgs e)
    {
        _dictWatcher?.Dispose();
        _instance?.Dispose();
        _session?.Dispose();
        _speaker?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        TrayIcons.Dispose();
        base.OnExit(e);
    }
}
