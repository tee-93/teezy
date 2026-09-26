using Teezy.Assistant;
using Teezy.Connectors;
using Teezy.Core.Calendar;
using Teezy.Core.Mail;
using Teezy.Core.Meetings;
using Teezy.Cleanup;
using Teezy.Core.Formatting;
using Teezy.Core.History;
using Teezy.Core.Quotes;
using Teezy.Core.Tasks;
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

    /// <summary>Name the Google OAuth client secret is filed under.</summary>
    /// <remarks>
    /// Google issues one even for desktop clients, where it cannot actually be kept secret —
    /// the binary is on the user's machine. Encrypted anyway, because "not confidential" is
    /// not the same as "belongs in a plain-text settings file".
    /// </remarks>
    internal const string GoogleSecretName = "google-client-secret";

    /// <summary>Name the Gmail app password is filed under.</summary>
    /// <remarks>
    /// Not an OAuth token and not scoped: an app password is full IMAP access to the mailbox,
    /// because Google does not issue a narrower one. All the more reason it is encrypted here
    /// rather than sitting in settings.json.
    /// </remarks>
    internal const string GmailPasswordName = "gmail-app-password";

    private VoiceSession? _session;
    private DictationController? _controller;
    private AssistantController? _assistant;
    private AssistantWindow? _assistantHud;
    private ClaudeAssistant? _claudeAssistant;
    private ClaudeNarrator? _narrator;
    private ConnectedAccounts? _calendars;
    private CombinedCalendar? _diary;
    private CombinedMailbox? _mail;
    private MeetingStore? _meetingStore;
    private MeetingRecorder? _meetingRecorder;
    private ClaudeMeetingSummariser? _meetingSummariser;
    private SpeakerDiariser? _diariser;
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
    private Forms.ToolStripMenuItem? _updateItem;
    private readonly Updater _updater = new();
    private bool _toldAboutUpdate;
    private HistoryStore? _history;
    private ISecretStore? _secrets;
    private SyncService? _sync;

    /// <summary>The task list, which the Tasks page, Home, reminders and sync all share.</summary>
    private readonly TaskStore _tasks = new();
    private readonly QuoteStore _quotes = new();
    private QuoteChasing? _chasing;
    private bool _following;

    private ReminderWindow? _reminders;
    private DispatcherTimer? _reminderTimer;

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
            // Already running: this launch was someone wanting the window. Bring it forward and go.
            _instance.AskFirstToShow();
            Shutdown();
            return;
        }

        _instance.Listen(() => Dispatch(() => ShowMainWindow()));

        _settings = TeezySettings.Load();

        // What 1.13's Outlook-window reading left behind. That route is gone.
        try { File.Delete(Path.Combine(Path.GetDirectoryName(TeezySettings.DefaultPath)!, "outlook-calendar.json")); }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException) { }
        _dictionary = new DictionaryStore(DictionaryStore.DefaultPath);
        EnsureDictionaryFileExists();
        WatchDictionaryFile();

        _hud = new HudWindow();
        _assistantHud = new AssistantWindow();
        BuildTray();

        // After the tray, so a ready update has somewhere to say so.
        _updater.Changed += state => Dispatch(() => OnUpdateChanged(state));
        _updater.Start();

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

        // Wrapped so sync hears about every key saved, from wherever it was saved.
        var secrets = new NotifyingSecretStore(new WindowsSecretStore());
        _secrets = secrets;

        // Early, so a newer setup from another computer is in place before anything below
        // reads a setting or a key.
        _sync = new SyncService(
            () => _settings,
            updated => ApplySettings(updated),
            _secrets,
            DictionaryStore.DefaultPath,
            [ApiKeyName, ElevenLabsKeyName, GoogleSecretName, GmailPasswordName],
            _tasks,
            _quotes);
        secrets.Changed += name => Dispatch(() => _sync?.SecretChanged(name));
        _tasks.Changed += () => Dispatch(() => _sync?.LocalChanged());
        _quotes.Changed += () => Dispatch(() => _sync?.LocalChanged());

        // A chase is an ordinary task, and this is what keeps one booked for every quote still
        // out: after any change to either list, and once at the start for whatever came in
        // from another computer overnight.
        _chasing = new QuoteChasing(_quotes, _tasks);
        _tasks.Changed += () => Dispatch(FollowQuotes);
        _quotes.Changed += () => Dispatch(FollowQuotes);
        FollowQuotes();
        _sync.Start();
        StartReminders();

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

        _calendars = new ConnectedAccounts(
            new TokenStore(_secrets),
            () => _settings.MicrosoftClientId is { Length: > 0 } ms ? ms : BuiltInApps.MicrosoftClientId,
            () => _settings.ReadMailEnabled,
            () => _settings.GoogleClientId is { Length: > 0 } g ? g : BuiltInApps.GoogleClientId,
            () => _secrets.Read(GoogleSecretName));

        // Deliberately a second Claude client rather than a flag on the first. This one is
        // given no tools because it is handed meeting subjects and message previews other
        // people wrote; that property survives only as long as the two stay separate objects.
        _narrator = new ClaudeNarrator(
            () => _settings.AssistantLlmEnabled ? _secrets.Read(ApiKeyName) : null,
            () => _settings.AssistantModel,
            TimeSpan.FromSeconds(Math.Clamp(_settings.AssistantTimeoutSeconds, 2, 30)));

        // Asked per question rather than built once, so connecting an account in Settings works
        // immediately instead of at the next launch. Held as fields because the dashboard reads
        // the same two — one calendar and one mailbox for the whole app, so the window and the
        // pill cannot end up describing different days.
        _diary = new CombinedCalendar(() => _calendars.Calendars(_settings.ConnectedAccounts));

        // Reading mail is a separate switch from connecting the account. An empty list means
        // the mail gate never claims anything, so a question falls through to the general tier
        // exactly as it did before.
        _mail = new CombinedMailbox(() => _settings.ReadMailEnabled ? Mailboxes() : []);
        // Meetings record through captures of their own, never dictation's: a meeting runs for
        // an hour, and dictating inside one must not find its microphone already taken.
        _meetingStore = new MeetingStore();
        _meetingRecorder = new MeetingRecorder(
            _meetingStore,
            microphone: () => new WindowsAudioCapture(CaptureSource.Microphone)
            {
                PreferredDeviceId = _settings.InputDeviceId,
            },
            speakers: () => new WindowsAudioCapture(CaptureSource.Speakers)
            {
                // Whatever Windows is playing to, unless a call is known to use another output.
                PreferredDeviceId = _settings.MeetingOutputId,
            });

        // Telling the far end's voices apart. Present either way; it declines quietly until
        // its two models have been downloaded.
        _diariser = new SpeakerDiariser();

        // Asked only when the user presses Summarise on a meeting. The key is read at that moment,
        // not gated on the cleanup or assistant switches: the button press is the decision.
        _meetingSummariser = new ClaudeMeetingSummariser(() => _secrets!.Read(ApiKeyName));

        _assistant = new AssistantController(
            _session, new WindowsCommandRunner(), _claudeAssistant, _diary, _mail, _narrator, _tasks,
            _quotes, () => _settings.QuoteCadence);

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
                _voiceUsage),

            // Loads its model on first use, not here: most people never switch it on.
            new KokoroSpeaker(new KokoroSynth(new KokoroModel())));

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
            Notify("TeezyFlow could not install its keyboard hook.", Forms.ToolTipIcon.Error);
        }

        // Open the window, at sign-in too: since 1.17 Home is the day's update, and the morning
        // is when it is wanted. (The Run entry still passes --startup, so a sign-in launch can
        // be told apart again if that ever needs a setting.)
        //
        // Deferred past a model download when there is one. On a fresh machine the download
        // window is the whole story at that moment, and two windows at once is not an
        // introduction.
        var modelPresent = ModelPaths.Resolve(_settings.ModelPath) is not null;

        if (modelPresent) ShowMainWindow();

        // The focus card comes back if it was open when TeezyFlow last closed.
        if (_settings.FocusOpen) ShowFocus();

        await LoadModelAsync().ConfigureAwait(false);

        // Back off the UI thread after the await, so this hops the dispatcher like everything
        // else that touches a window.
        if (!modelPresent) Dispatch(() => ShowMainWindow());
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
                MessageBox.Show(ex.Message, "TeezyFlow — model problem",
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
                "Couldn't type into that window. Elevated apps need TeezyFlow to be elevated too.",
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
            Text = "TeezyFlow — starting…",
        };

        var menu = new Forms.ContextMenuStrip();
        // Bold marks it as the default action, matching what a double-click does.
        var open = new Forms.ToolStripMenuItem("Open TeezyFlow", null, (_, _) => ShowMainWindow())
        {
            Font = new System.Drawing.Font(
                System.Drawing.SystemFonts.MenuFont!, System.Drawing.FontStyle.Bold),
        };
        // Hidden until an update has been downloaded and checked. Top of the menu, because it
        // is the one item that is news.
        _updateItem = new Forms.ToolStripMenuItem(string.Empty, null, (_, _) => RestartToUpdate()) { Visible = false };
        menu.Items.Add(_updateItem);
        menu.Items.Add(open);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Tasks…", null, (_, _) => ShowMainWindow(Page.Tasks));
        menu.Items.Add("Focus list", null, (_, _) => ShowFocus());
        menu.Items.Add("Morning briefing", null, (_, _) => _ = ShowBriefingAsync());
        menu.Items.Add("Settings…", null, (_, _) => ShowMainWindow(Page.Settings));
        menu.Items.Add("Dictionary…", null, (_, _) => ShowMainWindow(Page.Dictionary));
        menu.Items.Add("Meetings…", null, (_, _) => ShowMainWindow(Page.Meetings));

        // Only meaningful when setup was cancelled or failed, so it hides itself once the
        // model is loaded rather than sitting in the menu as a permanent puzzle.
        _downloadItem = new Forms.ToolStripMenuItem("Download speech model…", null,
            async (_, _) => await LoadModelAsync());
        menu.Items.Add(_downloadItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit TeezyFlow", null, (_, _) => Quit());
        TrayMenu.Apply(menu);
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowMainWindow();
    }

    // ---- Updates ----

    /// <summary>A newer version was downloaded and checked, or a check came and went.</summary>
    /// <remarks>
    /// TeezyFlow lives in the tray and is rarely quit, so "it installs when you quit" alone
    /// could mean never. The tray says so once per run, and offers the restart in its menu.
    /// </remarks>
    private void OnUpdateChanged(UpdateState state)
    {
        if (_updateItem is not null)
        {
            _updateItem.Visible = state.Ready;
            _updateItem.Text = $"Restart to update to {state.Version}";
        }

        if (state.Ready && !_toldAboutUpdate)
        {
            _toldAboutUpdate = true;
            Notify($"TeezyFlow {state.Version} is ready. It installs when you quit, or restart now from the tray menu.",
                Forms.ToolTipIcon.Info);
        }
    }

    /// <summary>Installs the ready update now and comes back afterwards.</summary>
    private void RestartToUpdate()
    {
        _focus?.Quitting();
        if (_updater.Install(relaunch: true)) Shutdown();
    }

    /// <summary>Quit, installing a ready update on the way out, as Fivebar does.</summary>
    internal void Quit()
    {
        _focus?.Quitting();
        _updater.Install(relaunch: false);
        Shutdown();
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
            calendars: _calendars,
            diary: _diary,
            mail: _mail,
            meetingStore: _meetingStore,
            meetingRecorder: _meetingRecorder,
            meetingSummariser: _meetingSummariser,
            diariser: _diariser,
            updater: _updater,
            restartToUpdate: RestartToUpdate,
            sync: _sync,
            tasks: _tasks,
            quotes: _quotes,

            // The key is the cleanup tier's: the same Anthropic account. Called only when a
            // button under a pasted email is pressed.
            advisor: new Teezy.Assistant.ClaudeMailAdvisor(
                () => _secrets?.Read(ApiKeyName),
                () => _settings.AssistantModel,
                TimeSpan.FromSeconds(45)),
            showBriefing: () => _ = ShowBriefingAsync(),
            showFocus: ShowFocus);

        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.ShowPage(page);
        _main.RefreshCurrentPage();
    }

    /// <summary>Opens the window at one task, from a reminder.</summary>
    private void ShowTask(string id)
    {
        ShowMainWindow(Page.Tasks);
        _main!.ShowTask(id);
    }

    /// <summary>
    /// Looks every half minute for tasks whose reminder time has come, and puts them on the card.
    /// </summary>
    /// <remarks>
    /// Each is marked as reminded here before it is shown, so it is shown once on this computer;
    /// the mark does not travel, so each computer that is on at the time shows it.
    /// </remarks>
    private void StartReminders()
    {
        _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _reminderTimer.Tick += (_, _) => CheckReminders();
        _reminderTimer.Start();

        // A first look shortly after start, once the tray is up, for anything missed while off.
        var first = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        first.Tick += (_, _) => { first.Stop(); CheckReminders(); };
        first.Start();
    }

    private void CheckReminders()
    {
        CheckBriefing();

        var due = TaskPlan.DueForReminder(_tasks.Visible, DateTimeOffset.Now);
        if (due.Count == 0) return;

        foreach (var task in due) _tasks.MarkReminded(task.Id);
        _reminders ??= new ReminderWindow(_tasks, ShowTask);
        _reminders.Remind(due);
    }

    /// <summary>
    /// Books, retimes or cancels the chases the quotes need. Guarded against itself: it changes
    /// the task list, which raises the change that called it.
    /// </summary>
    private void FollowQuotes()
    {
        if (_chasing is null || _following) return;

        _following = true;
        try { _chasing.Follow(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        finally { _following = false; }
    }

    // ============================== the focus list ==============================

    private FocusWindow? _focus;

    /// <summary>Opens the focus card, or brings it forward if it is already out.</summary>
    internal void ShowFocus()
    {
        if (_focus is { IsLoaded: true })
        {
            if (_focus.WindowState == WindowState.Minimized) _focus.WindowState = WindowState.Normal;
            _focus.Activate();
            return;
        }

        _focus = new FocusWindow(_tasks, () => _settings, ApplySettings,
            id => { if (id.Length == 0) ShowMainWindow(Page.Tasks); else ShowTask(id); },
            MatchCategory);
        _focus.Closed += (_, _) => _focus = null;
        _focus.Show();
        _focus.Activate();
    }

    /// <summary>A #category typed in the focus card, in the list's spelling; added to the list if new.</summary>
    private string? MatchCategory(string? typed)
    {
        if (typed is not { Length: > 0 }) return null;
        var match = _settings.TaskCategories.FirstOrDefault(c => c.Equals(typed, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;
        ApplySettings(_settings with { TaskCategories = [.. _settings.TaskCategories, typed] });
        return typed;
    }

    // ============================== the morning briefing ==============================

    private BriefingWindow? _briefing;
    private Teezy.Assistant.ClaudeBriefer? _briefer;

    /// <summary>On the reminder timer: shows the briefing once a day, at the first look after its time.</summary>
    private void CheckBriefing()
    {
        var now = DateTimeOffset.Now;
        if (!_settings.BriefingOn || _briefing is { IsVisible: true }) return;
        if (!Teezy.Core.Home.MorningBriefing.IsDue(now, _settings.BriefingTime, _settings.BriefingWeekends, _settings.BriefingShownOn)) return;

        // Marked first, so a slow calendar read cannot let the next tick show it twice.
        ApplySettings(_settings with { BriefingShownOn = DateOnly.FromDateTime(now.LocalDateTime) });
        _ = ShowBriefingAsync();
    }

    /// <summary>Builds the briefing from this computer's tasks, plus the calendar and mail where connected, and shows it.</summary>
    internal async Task ShowBriefingAsync()
    {
        var now = DateTimeOffset.Now;
        var today = DateOnly.FromDateTime(now.LocalDateTime);

        IReadOnlyList<CalendarEvent>? events = null;
        if (_diary is { IsConnected: true } diary)
        {
            try
            {
                var reading = await diary.BetweenAsync(CalendarWeek.Midnight(today), CalendarWeek.Midnight(today.AddDays(1)));
                events = CalendarWeek.On(reading.Events, today);
            }
            catch (CalendarUnavailableException) { }
        }

        Teezy.Core.Mail.MailReading? mail = null;
        if (_mail is { IsConnected: true } box)
        {
            try
            {
                var (since, atMost) = MailAnswer.Window(MailAsk.Unread, now);
                mail = await box.RecentAsync(since, atMost);
            }
            catch (MailUnavailableException) { }
        }

        IReadOnlyList<DateTimeOffset> meetings;
        try { meetings = [.. (_meetingStore?.List() ?? []).Select(m => m.Info.Started)]; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { meetings = []; }

        var usage = Teezy.Core.History.UsageStats.From(_history?.Load() ?? [], today);
        var snapshot = new Teezy.Core.Home.HomeSnapshot(now, _tasks.Visible, usage, meetings, events, mail);
        var briefing = Teezy.Core.Home.MorningBriefing.For(snapshot, _settings.NoteAuthor);

        _briefer ??= new Teezy.Assistant.ClaudeBriefer(
            () => _secrets?.Read(ApiKeyName), () => _settings.AssistantModel, TimeSpan.FromSeconds(30));
        Func<Task<string?>>? summarise = _settings.BriefingSummary
            ? () => _briefer.SummariseAsync(Teezy.Core.Home.MorningBriefing.Material(snapshot), now)
            : null;
        Action<string>? speak = _speaker is { IsAvailable: true } ? text => _ = _speaker.SpeakAsync(text) : null;

        _briefing?.Close();
        _briefing = new BriefingWindow(briefing, _tasks, ShowTask, () => ShowMainWindow(), summarise, speak);
        _briefing.Show();
    }

    /// <summary>Every mailbox that is set up, whichever way it was set up.</summary>
    /// <remarks>
    /// The two arrive completely differently — Microsoft through OAuth as a connected account,
    /// Gmail through IMAP with an app password and no account row at all — and everything above
    /// this deals in <see cref="IMailbox"/> and never learns the difference.
    /// </remarks>
    private IReadOnlyList<IMailbox> Mailboxes()
    {
        List<IMailbox> boxes = [.. _calendars!.Mailboxes(_settings.ConnectedAccounts)];

        var gmail = new ImapMailbox(
            () => _settings.GmailAddress,
            () => _secrets!.Read(GmailPasswordName));

        if (gmail.IsConnected) boxes.Add(gmail);

        return boxes;
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
            _speaker.PreferredVoice = _settings.VoiceProvider switch
            {
                VoiceProvider.ElevenLabs => _settings.ElevenLabsVoice,
                VoiceProvider.Kokoro => _settings.KokoroVoice,
                _ => _settings.SpeechVoice,
            };
        }

        SetTrayState($"Ready — hold {_settings.Hotkey.Display} to dictate", _modelReady);

        // Last, so a change is saved here before it is sent anywhere else.
        _sync?.LocalChanged();
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
        _tray?.ShowBalloonTip(4000, "TeezyFlow", message, icon);


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
        // A meeting still recording is stopped and saved rather than abandoned mid-file.
        _meetingRecorder?.Dispose();
        _session?.Dispose();
        _speaker?.Dispose();
        _diariser?.Dispose();
        _updater.Dispose();
        _sync?.Dispose();
        _reminderTimer?.Stop();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        TrayIcons.Dispose();
        base.OnExit(e);
    }
}
