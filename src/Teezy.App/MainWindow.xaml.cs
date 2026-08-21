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
    private readonly Func<TeezySettings> _settings;
    private readonly Action<TeezySettings> _saveSettings;
    private readonly ParakeetTranscriber? _transcriber;

    private HomeView? _home;
    private InsightsView? _insights;

    public MainWindow(
        HistoryStore history,
        Func<TeezySettings> settings,
        Action<TeezySettings> saveSettings,
        ParakeetTranscriber? transcriber)
    {
        InitializeComponent();
        _history = history;
        _settings = settings;
        _saveSettings = saveSettings;
        _transcriber = transcriber;

        Icon = LogoImage.Create(48);
        ShowHome();
    }

    /// <summary>Re-reads history so a dictation made while the window was open shows up.</summary>
    public void RefreshCurrentPage()
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

        if (sender == NavHome) ShowHome();
        else if (sender == NavInsights) ShowInsights();
        else if (sender == NavDictionary) OpenDictionary();
        else if (sender == NavSettings) OpenSettings();
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

    private void OpenDictionary()
    {
        // The dictionary is a hand-maintained text file, and a text editor is a better
        // editor for it than anything worth building here yet.
        try
        {
            Process.Start(new ProcessStartInfo(DictionaryStore.DefaultPath) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            MessageBox.Show("No app is associated with .txt files.", "Teezy",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            NavHome.IsChecked = true;
        }
    }

    private void OpenSettings()
    {
        var window = new SettingsWindow(_settings(), _transcriber, _transcriber?.IsLoaded ?? false)
        {
            Owner = this,
        };
        window.SettingsChanged += _saveSettings;
        window.ShowDialog();
        NavHome.IsChecked = true;
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
