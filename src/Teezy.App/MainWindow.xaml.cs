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
using Teezy.Speech;

namespace Teezy.App;

/// <summary>The app window: history, insights and settings behind a nav rail.</summary>
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

    private HomeView? _home;
    private InsightsView? _insights;
    private DictionaryView? _dictionaryView;
    private SettingsView? _settingsView;

    public MainWindow(
        HistoryStore history,
        DictionaryStore dictionary,
        Func<TeezySettings> settings,
        Action<TeezySettings> saveSettings,
        ParakeetTranscriber? transcriber,
        IAutostart? autostart = null,
        IHotkeyCapture? capture = null,
        ISecretStore? secrets = null,
        ClaudeFormatter? claude = null)
    {
        InitializeComponent();
        _history = history;
        _dictionary = dictionary;
        _settings = settings;
        _saveSettings = saveSettings;
        _transcriber = transcriber;
        _autostart = autostart;
        _capture = capture;
        _secrets = secrets;
        _claude = claude;

        // Icon deliberately not set: WPF falls back to the executable icon resource, which
        // carries every size, so Windows can pick the right one per context. Assigning a
        // single rendered bitmap here would give the taskbar one size to scale from.
        ShowHome();
    }

    /// <summary>Re-reads everything the current page shows. Used when the window is opened.</summary>
    public void RefreshCurrentPage()
    {
        switch (PageHost.Content)
        {
            case HomeView home: home.Refresh(); break;
            case InsightsView insights: insights.Refresh(); break;
            case DictionaryView dictionary: dictionary.Refresh(); break;
            case SettingsView settings: settings.Refresh(); break;
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
        else if (sender == NavInsights) ShowInsights();
        else if (sender == NavDictionary) ShowDictionary();
        else if (sender == NavSettings) ShowSettings();
    }

    private void ShowHome()
    {
        _home ??= new HomeView(_history);
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
            default: NavHome.IsChecked = true; break;
        }
    }

    private void ShowSettings()
    {
        _settingsView ??= new SettingsView(
            _settings, _saveSettings, _transcriber, _autostart, _capture, _secrets, _claude,

            // The apps you have actually dictated into, so a rule can be added by picking
            // rather than by knowing that Outlook reports itself as "OUTLOOK".
            knownApps: () => [.. UsageStats
                .From(_history.Load(), DateOnly.FromDateTime(DateTime.Today))
                .Apps.Select(a => a.App)]);
        _settingsView.Refresh();
        PageHost.Content = _settingsView;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Hide, never close. Teezy lives in the tray; disposing this window would mean
        // rebuilding it, and quitting on a window close would strand the hotkey.
        e.Cancel = true;
        Hide();
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
}
