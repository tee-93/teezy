using System;
using System.IO;
using System.Threading.Tasks;
using Teezy.Core;
using Teezy.Core.Dictionary;

namespace Teezy.App;

public partial class App
{
    /// <summary>Mirrors controller state into the HUD and the two chimes.</summary>
    private void OnStateChanged(DictationState state)
    {
        if (_settings.ShowHud) _hud!.ShowState(state);
        else _hud!.Hide();

        if (!_settings.SoundEnabled) return;

        // Tied to Listening and Finishing rather than to the key events, so a tone marks the
        // moment the microphone is actually open rather than the moment it was asked to
        // open. On a cold start those are meaningfully different.
        if (state == DictationState.Listening) Chime.Start();
        else if (state == DictationState.Finishing) Chime.Stop();
    }

    /// <summary>Reloads the dictionary when the user edits the file in their own editor.</summary>
    private void WatchDictionaryFile()
    {
        var path = DictionaryStore.DefaultPath;

        _dictWatcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        _dictWatcher.Changed += (_, _) =>
        {
            // Editors commonly write a save in several steps, so one Ctrl+S can raise two or
            // three events and the file may be briefly locked. Settle first, and treat a
            // locked file as "try again on the next event" rather than as an error.
            _ = Task.Delay(250).ContinueWith(_ =>
            {
                try { _dictionary!.Reload(); }
                catch (IOException) { }
            }, TaskScheduler.Default);
        };
    }
}
