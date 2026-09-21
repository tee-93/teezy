using System;
using System.IO;
using System.Linq;
using System.Windows;
using Teezy.Core.Calendar;
using Teezy.Core.Sync;

namespace Teezy.App;

/// <summary>Settings ▸ Sync, and the calendar-file row under Accounts.</summary>
public partial class SettingsView
{
    private SyncService? _sync;

    /// <summary>Hooked up by the window once it has the service; the view is built before sync is.</summary>
    internal void AttachSync(SyncService? sync)
    {
        if (_sync is not null || sync is null) return;
        _sync = sync;
        _sync.Changed += status => Dispatcher.BeginInvoke(() => ShowSync(status));
        ShowSync(_sync.Status);
    }

    private void ShowSync(SyncStatus status)
    {
        var on = _sync?.IsOn == true;

        SyncStatusText.Text = status.Message;
        SyncDot.Fill = status.Problem ? Brand.Brush("Danger") : on ? Brand.Accent : Brand.Faint;

        SyncFolderText.Text = on ? _read().SyncFolder : string.Empty;
        SyncFolderText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SyncOnActions.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        SyncSetup.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnChooseSyncFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the folder your computers share",
            InitialDirectory = Environment.GetEnvironmentVariable("OneDrive") ?? string.Empty,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true) SyncFolderBox.Text = dialog.FolderName;
    }

    private void OnSyncSetupChanged(object sender, RoutedEventArgs e)
    {
        var passphrase = SyncPassphraseBox.Password;
        var again = SyncPassphraseAgainBox.Password;
        var matches = passphrase.Length > 0 && passphrase == again;

        SyncPassphraseHint.Text = again.Length > 0 && !matches
            ? "The two passphrases don’t match."
            : passphrase.Length is > 0 and < 8
                ? "Use at least 8 characters. A short sentence is easy to remember and hard to guess."
                : "Type it twice. It locks the file, so OneDrive and anyone else see only scrambled data. It can’t be recovered: if it’s forgotten, turn sync off everywhere and start again.";

        TurnOnSyncButton.IsEnabled = _sync is not null
                                     && SyncFolderBox.Text.Trim().Length > 0
                                     && matches
                                     && passphrase.Length >= 8;
    }

    private void OnTurnOnSync(object sender, RoutedEventArgs e)
    {
        if (_sync is null) return;

        TurnOnSyncButton.IsEnabled = false;
        try
        {
            _sync.TurnOn(SyncFolderBox.Text.Trim(), SyncPassphraseBox.Password);
            SyncPassphraseBox.Clear();
            SyncPassphraseAgainBox.Clear();

            // A joined setup may have changed almost everything on the page.
            Refresh();
        }
        catch (SyncUnlockException problem)
        {
            SyncSetupNote.Text = problem.Message;
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            SyncSetupNote.Text = $"That folder can’t be used: {problem.Message}";
        }
        finally
        {
            OnSyncSetupChanged(sender, e);
        }
    }

    private void OnTurnOffSync(object sender, RoutedEventArgs e) => _sync?.TurnOff();

    private void OnSyncNow(object sender, RoutedEventArgs e) => _sync?.SyncNow();

    // ---- reading New Outlook ----

    private OutlookWatcher? _outlook;

    /// <summary>Hooked up by the window once it has the watcher.</summary>
    internal void AttachOutlook(OutlookWatcher? outlook)
    {
        if (_outlook is not null || outlook is null) return;
        _outlook = outlook;
        _outlook.Changed += status => Dispatcher.BeginInvoke(() => ShowOutlook(status));
        ShowOutlook(_outlook.Status);
    }

    private void ShowOutlook(OutlookReadStatus status)
    {
        var on = _read().ReadOutlookWindow;
        OutlookReadBox.IsChecked = on;
        OutlookReadRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        OutlookReadText.Text = status.Message;
        OutlookReadDot.Fill = status.Problem ? Brand.Brush("CautionBorder") : Brand.Accent;
    }

    private async void OnOutlookReadToggled(object sender, RoutedEventArgs e)
    {
        if (_calendars is null) return;
        var on = OutlookReadBox.IsChecked == true;
        var settings = _read();
        var others = settings.ConnectedAccounts.Where(a => a.Id != Connectors.ConnectedAccounts.OutlookWindowId).ToList();

        if (on)
        {
            var account = _calendars.ConnectOutlookWindow(OutlookWatcher.CachePath);
            _write(settings with { ReadOutlookWindow = true, ConnectedAccounts = [.. others, account] });
        }
        else
        {
            _write(settings with { ReadOutlookWindow = false, ConnectedAccounts = others });
            try { File.Delete(OutlookWatcher.CachePath); } catch (IOException) { }
        }

        Refresh();
        if (_outlook is not null)
        {
            ShowOutlook(_outlook.Status);
            if (on) await _outlook.ReadAsync();
        }
    }

    private async void OnReadOutlookNow(object sender, RoutedEventArgs e)
    {
        if (_outlook is not null) await _outlook.ReadAsync();
    }

    // ---- the calendar file ----

    private void OnCalendarFileTyped(object sender, RoutedEventArgs e) =>
        AddCalendarFileButton.IsEnabled = _calendars is not null && CalendarFileBox.Text.Trim().Length > 0;

    private void OnChooseCalendarFile(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the calendar file your flow writes",
            Filter = "Calendar file (*.json)|*.json|All files|*.*",
            InitialDirectory = Environment.GetEnvironmentVariable("OneDriveCommercial") ?? string.Empty,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true) CalendarFileBox.Text = dialog.FileName;
    }

    private void OnAddCalendarFile(object sender, RoutedEventArgs e)
    {
        var path = CalendarFileBox.Text.Trim().Trim('"');
        if (_calendars is null || path.Length == 0) return;

        CalendarWarning.Visibility = Visibility.Collapsed;
        try
        {
            // Work to begin with: this route exists for a work calendar. The row's picker changes it.
            var added = _calendars.ConnectFile(path, "Work calendar", CalendarProfile.Work);

            var settings = _read();
            _write(settings with { ConnectedAccounts = [.. settings.ConnectedAccounts, added] });

            CalendarFileBox.Clear();
            Refresh();
        }
        catch (Exception problem) when (problem is CalendarUnavailableException or IOException
                                            or UnauthorizedAccessException)
        {
            CalendarWarningText.Text = problem.Message;
            CalendarWarning.Visibility = Visibility.Visible;
        }
    }
}
