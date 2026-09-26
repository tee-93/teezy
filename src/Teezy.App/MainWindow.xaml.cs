using Teezy.Core.Quotes;
using Teezy.Core.Tasks;
using System.Linq;
using Teezy.Cleanup;
using Teezy.Core.Hotkeys;
using Teezy.Core.Abstractions;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Teezy.Core;
using Teezy.Core.Dictionary;
using Teezy.Core.History;
using Teezy.Core.Voice;
using Teezy.Connectors;
using Teezy.Core.Calendar;
using Teezy.Core.Mail;
using Teezy.Core.Meetings;
using Teezy.Speech;

namespace Teezy.App;

/// <summary>The app window: pages as tabs along a top strip, and a status bar.</summary>
/// <remarks>
/// Teezy works entirely from the tray, so this window is never required — it is opened, read
/// and closed. Closing it therefore hides rather than exits, and the pages rebuild their
/// content on show instead of subscribing to live updates: a window nobody is looking at
/// should not be doing work.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly HistoryStore _history;
    private readonly DictionaryStore _dictionary;
    private readonly Func<TeezySettings> _settings;
    private readonly Action<TeezySettings> _saveSettings;
    private readonly ParakeetTranscriber? _transcriber;
    private readonly IAutostart? _autostart;
    private readonly IHotkeyCapture? _capture;
    private readonly ISecretStore? _secrets;
    private readonly ClaudeFormatter? _claude;
    private readonly Func<IAudioCapture>? _microphone;
    private readonly ISpeaker? _speaker;
    private readonly VoiceUsage? _usage;
    private readonly ConnectedAccounts? _calendars;
    private readonly CombinedCalendar? _diary;
    private readonly CombinedMailbox? _mail;
    private readonly MeetingStore? _meetingStore;
    private readonly MeetingRecorder? _meetingRecorder;
    private readonly IMeetingSummariser? _meetingSummariser;
    private readonly IDiariser? _diariser;

    private HomeView? _home;
    private TranscriptsView? _transcripts;
    private InsightsView? _insights;
    private DictionaryView? _dictionaryView;
    private SettingsView? _settingsView;
    private MeetingsView? _meetingsView;
    private QuotesView? _quotesView;

    public MainWindow(
        HistoryStore history,
        DictionaryStore dictionary,
        Func<TeezySettings> settings,
        Action<TeezySettings> saveSettings,
        ParakeetTranscriber? transcriber,
        IAutostart? autostart = null,
        IHotkeyCapture? capture = null,
        ISecretStore? secrets = null,
        ClaudeFormatter? claude = null,
        Func<IAudioCapture>? microphone = null,
        ISpeaker? speaker = null,
        VoiceUsage? usage = null,
        ConnectedAccounts? calendars = null,
        CombinedCalendar? diary = null,
        CombinedMailbox? mail = null,
        MeetingStore? meetingStore = null,
        MeetingRecorder? meetingRecorder = null,
        IMeetingSummariser? meetingSummariser = null,
        IDiariser? diariser = null,
        Updater? updater = null,
        Action? restartToUpdate = null,
        SyncService? sync = null,
        TaskStore? tasks = null,
        QuoteStore? quotes = null,
        IMailAdvisor? advisor = null,
        Action? showBriefing = null,
        Action? showFocus = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _history = history;
        _dictionary = dictionary;
        _settings = settings;
        _saveSettings = saveSettings;
        _transcriber = transcriber;
        _autostart = autostart;
        _capture = capture;
        _secrets = secrets;
        _claude = claude;
        _microphone = microphone;
        _speaker = speaker;
        _usage = usage;
        _calendars = calendars;
        _diary = diary;
        _mail = mail;
        _meetingStore = meetingStore;
        _meetingRecorder = meetingRecorder;
        _meetingSummariser = meetingSummariser;
        _diariser = diariser;
        _updater = updater;
        _restartToUpdate = restartToUpdate;
        _sync = sync;
        _tasks = tasks;
        _quotes = quotes;
        _advisor = advisor;
        _showBriefing = showBriefing;
        _showFocus = showFocus;

        // Icon deliberately not set: WPF falls back to the executable icon resource, which
        // carries every size, so Windows can pick the right one per context. Assigning a
        // single rendered bitmap here would give the taskbar one size to scale from.
        ShowHome();

        var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "unknown version";
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        StatusVersion.Text = $"TeezyFlow {version} · {arch}";

        // The model loads in the background after launch, so the status bar has to notice when
        // it finishes. It checks only while the window is showing: a window nobody is looking at
        // should not be doing work.
        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) { UpdateStatus(); _statusTimer.Start(); }
            else _statusTimer.Stop();
        };
        UpdateStatus();

        if (_updater is not null)
        {
            _updater.Changed += state => Dispatcher.BeginInvoke(() => ShowUpdate(state));
            ShowUpdate(_updater.State);
        }
    }

    private readonly System.Windows.Threading.DispatcherTimer _statusTimer;
    private readonly Updater? _updater;
    private readonly Action? _restartToUpdate;
    private readonly SyncService? _sync;
    private readonly TaskStore? _tasks;
    private readonly QuoteStore? _quotes;
    private readonly IMailAdvisor? _advisor;
    private readonly Action? _showBriefing;
    private readonly Action? _showFocus;
    private TasksView? _tasksView;

    /// <summary>Closed with its 'Later' button, which lasts until the next version.</summary>
    private Version? _hiddenUpdate;

    private void ShowUpdate(UpdateState state)
    {
        UpdateVersion.Text = $"TeezyFlow {state.Version}";
        UpdateBar.Visibility = state.Ready && state.Version != _hiddenUpdate
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnRestartToUpdate(object sender, RoutedEventArgs e) => _restartToUpdate?.Invoke();

    private void OnHideUpdate(object sender, RoutedEventArgs e)
    {
        _hiddenUpdate = _updater?.State.Version;
        UpdateBar.Visibility = Visibility.Collapsed;
    }

    /// <summary>The status bar: whether dictation will work right now, and how to start it.</summary>
    private void UpdateStatus()
    {
        var loaded = _transcriber?.IsLoaded == true;
        StatusDot.Fill = loaded ? Brand.Accent : Brand.Faint;
        StatusModel.Text = loaded ? "Ready" : "Loading the speech model…";

        var settings = _settings();
        StatusHotkeys.Text = settings.AssistantHotkey.IsEmpty
            ? $"Hold {settings.Hotkey.Display} to dictate"
            : $"Hold {settings.Hotkey.Display} to dictate, {settings.AssistantHotkey.Display} for the assistant";
    }

    /// <summary>Re-reads everything the current page shows. Used when the window is opened.</summary>
    public void RefreshCurrentPage()
    {
        UpdateStatus();

        switch (PageHost.Content)
        {
            case HomeView home: home.Refresh(); break;
            case InsightsView insights: insights.Refresh(); break;
            case DictionaryView dictionary: dictionary.Refresh(); break;
            case SettingsView settings: settings.Refresh(); break;
            case MeetingsView meetings: meetings.Refresh(); break;
            case TasksView tasks: tasks.Refresh(); break;
        }
    }

    /// <summary>Shows an utterance that landed while the window was open.</summary>
    /// <remarks>
    /// Deliberately does not touch the dictionary page. Dictating changes history and stats,
    /// not the dictionary, and reloading it under someone who is part-way through editing an
    /// entry would be a small betrayal for no benefit.
    /// </remarks>
    public void RefreshAfterDictation()
    {
        switch (PageHost.Content)
        {
            case HomeView home: home.Refresh(); break;
            case InsightsView insights: insights.Refresh(); break;
        }
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        // Checked fires during InitializeComponent, before the fields exist.
        if (PageHost is null) return;

        // Leaving settings must cancel any running hotkey capture, or it would swallow the
        // next real press.
        if (PageHost.Content is SettingsView leaving && sender != NavSettings) leaving.Leaving();

        if (sender == NavHome) ShowHome();
        else if (sender == NavTasks) ShowTasks();
        else if (sender == NavTranscripts) ShowTranscripts();
        else if (sender == NavMeetings) ShowMeetings();
        else if (sender == NavQuotes) ShowQuotes();
        else if (sender == NavInsights) ShowInsights();
        else if (sender == NavDictionary) ShowDictionary();
        else if (sender == NavSettings) ShowSettings();
    }

    private void ShowTasks()
    {
        if (_tasks is null) return;

        _tasksView ??= new TasksView(_tasks, _advisor, _settings, _saveSettings) { OpenTaskSettings = () => ShowSettingsTab("TabTasks"), ShowFocus = _showFocus };
        _tasksView.Refresh();
        PageHost.Content = _tasksView;
    }

    /// <summary>Opens the Tasks page at one task — from Home, or from a reminder.</summary>
    public void ShowTask(string id)
    {
        NavTasks.IsChecked = true;
        ShowTasks();
        _tasksView?.Select(id);
    }

    private void ShowQuotes()
    {
        if (_tasks is null) return;
        _quotesView ??= new QuotesView(_quotes!, _tasks, _settings, _saveSettings, ShowTask);
        _quotesView.Refresh();
        PageHost.Content = _quotesView;
    }

    private void ShowMeetings()
    {
        if (_meetingStore is null || _meetingRecorder is null) return;

        _meetingsView ??= new MeetingsView(
            _meetingStore, _meetingRecorder, _transcriber, _meetingSummariser, _settings, _diariser);
        _meetingsView.Refresh();
        PageHost.Content = _meetingsView;
    }
    private void ShowTranscripts()
    {
        _transcripts ??= new TranscriptsView(_history);
        _transcripts.Refresh();
        PageHost.Content = _transcripts;
    }

    private void ShowHome()
    {
        _home ??= new HomeView(
            _history,
            _tasks ?? new TaskStore(),
            _settings,
            _saveSettings,
            new HomeActions(ShowTask, ShowPage, MatchCategory, _showBriefing, _showFocus),
            () => _settings().Hotkey.Display,
            _diary,
            _mail,
            _meetingStore,
            _quotes);
        _home.Refresh();
        PageHost.Content = _home;
    }

    private void ShowInsights()
    {
        _insights ??= new InsightsView(_history);
        _insights.Refresh();
        PageHost.Content = _insights;
    }

    private void ShowDictionary()
    {
        _dictionaryView ??= new DictionaryView(_dictionary);
        _dictionaryView.Refresh();
        PageHost.Content = _dictionaryView;
    }

    /// <summary>Opens the window directly on a page, for the tray menu.</summary>
    public void ShowPage(Page page)
    {
        switch (page)
        {
            case Page.Insights: NavInsights.IsChecked = true; break;
            case Page.Dictionary: NavDictionary.IsChecked = true; break;
            case Page.Settings: NavSettings.IsChecked = true; break;
            case Page.Meetings: NavMeetings.IsChecked = true; break;
            case Page.Quotes: NavQuotes.IsChecked = true; break;
            case Page.Tasks: NavTasks.IsChecked = true; break;
            default: NavHome.IsChecked = true; break;
        }
    }

    /// <summary>Opens Settings at one section, e.g. "TabTasks" from the Tasks page's Manage categories.</summary>
    private void ShowSettingsTab(string tab)
    {
        NavSettings.IsChecked = true;
        ShowSettings();
        _settingsView?.ShowTab(tab);
    }

    /// <summary>A #category typed in quick add, in the list's spelling; added to the list if new.</summary>
    private string? MatchCategory(string? typed)
    {
        if (typed is not { Length: > 0 }) return null;
        var settings = _settings();
        var match = settings.TaskCategories.FirstOrDefault(c => c.Equals(typed, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        _saveSettings(settings with { TaskCategories = [.. settings.TaskCategories, typed] });
        return typed;
    }

    private void ShowSettings()
    {
        _settingsView ??= new SettingsView(
            _settings, _saveSettings, _transcriber, _autostart, _capture, _secrets, _claude,

            // The apps you have actually dictated into, so a rule can be added by picking
            // rather than by knowing that Outlook reports itself as "OUTLOOK".
            knownApps: () => [.. UsageStats
                .From(_history.Load(), DateOnly.FromDateTime(DateTime.Today))
                .Apps.Select(a => a.App)],
            microphone: _microphone,
            speaker: _speaker,
            usage: _usage,
            calendars: _calendars,
            updater: _updater,
            restartToUpdate: _restartToUpdate);
        _settingsView.AttachSync(_sync);
        _settingsView.AttachTasks(_tasks);
        _settingsView.ShowBriefing = _showBriefing;
        _settingsView.Refresh();
        PageHost.Content = _settingsView;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Never actually closed: the window is built once and kept. What the X does is the
        // user's choice — asked the first time — between keeping TeezyFlow running, minimised
        // on the taskbar like any open application, and quitting it.
        //
        // The settings page is told either way: it may be holding the microphone open for a
        // level meter, and a minimised window has no business still recording.
        e.Cancel = true;
        _settingsView?.Leaving();

        var action = _settings().CloseAction;
        if (action == CloseAction.Ask)
        {
            var dialog = new CloseDialog(this);
            if (dialog.ShowDialog() != true || dialog.Choice is not { } chosen) return;
            action = chosen;
            if (dialog.Remember) _saveSettings(_settings() with { CloseAction = chosen });
        }

        if (action == CloseAction.Quit) ((App)Application.Current).Quit();
        else WindowState = WindowState.Minimized;

        base.OnClosing(e);
    }
}

/// <summary>Pages reachable from outside the window.</summary>
public enum Page
{
    Home,
    Insights,
    Dictionary,
    Settings,
    Meetings,
    Quotes,
    Tasks,
}
