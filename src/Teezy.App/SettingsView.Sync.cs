using System;
using System.IO;
using System.Linq;
using System.Windows;
using Teezy.Core.Sync;

namespace Teezy.App;

/// <summary>Settings ▸ Sync.</summary>
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
            InitialDirectory = SuggestedSyncFolder(),
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true) SyncFolderBox.Text = dialog.FolderName;
    }

    /// <summary>
    /// Where the folder picker opens: Google Drive's "My Drive" if it is mounted, else OneDrive.
    /// </summary>
    /// <remarks>
    /// Google Drive for desktop mounts as its own drive letter, usually G:, with "My Drive" at its
    /// root; it is found by looking, since the letter is the user's choice.
    /// </remarks>
    private static string SuggestedSyncFolder()
    {
        foreach (var drive in System.IO.DriveInfo.GetDrives())
        {
            try
            {
                var myDrive = System.IO.Path.Combine(drive.RootDirectory.FullName, "My Drive");
                if (drive.IsReady && System.IO.Directory.Exists(myDrive)) return myDrive;
            }
            catch (System.IO.IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return Environment.GetEnvironmentVariable("OneDrive") ?? string.Empty;
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

}
