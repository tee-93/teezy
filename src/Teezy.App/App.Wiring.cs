using System;
using System.IO;
using System.Threading.Tasks;
using Teezy.Core;
using Teezy.Core.Abstractions;
using Teezy.Core.Dictionary;
using Teezy.Speech;
using Forms = System.Windows.Forms;

namespace Teezy.App;

public partial class App
{
    /// <summary>Mirrors controller state into the HUD and the two chimes.</summary>
    private void OnStateChanged(DictationState state)
    {
        if (_settings.ShowHud) _hud!.ShowState(state);
        else _hud!.Hide();

        if (state == DictationState.Listening) WarnIfMicrophoneMissing();

        if (!_settings.SoundEnabled) return;

        // Tied to Listening and Finishing rather than to the key events, so a tone marks the
        // moment the microphone is actually open rather than the moment it was asked to
        // open. On a cold start those are meaningfully different.
        if (state == DictationState.Listening) Chime.Start();
        else if (state == DictationState.Finishing) Chime.Stop();
    }

    /// <summary>Says so, once, when the chosen microphone was not the one that opened.</summary>
    /// <remarks>
    /// The fallback itself is the right behaviour — an unplugged headset must not mean a dead
    /// hotkey. But falling back silently would reproduce the exact problem the picker exists
    /// to solve: dictating for a week through a microphone you did not choose and cannot see.
    /// Once per run, because it is the same news on every utterance.
    /// </remarks>
    private void WarnIfMicrophoneMissing()
    {
        if (_warnedAboutMicrophone || _audio?.UsingFallbackDevice != true) return;

        _warnedAboutMicrophone = true;

        var chosen = _settings.InputDeviceName ?? "The microphone you chose";
        Notify($"{chosen} is not available. Using {_audio.DeviceName} instead.",
            Forms.ToolTipIcon.Warning);
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
                string? before = null;
                try
                {
                    before = _dictionary!.Hotwords();
                    _dictionary.Reload();
                }
                catch (IOException) { return; }

                // Corrections apply on the next utterance for free; hints are compiled into
                // the recogniser, so a changed hint list means rebuilding it. Only when the
                // hints actually changed — editing a correction should not cost a model load.
                if (_dictionary.Hotwords() != before) ReloadRecogniserForHints();
            }, TaskScheduler.Default);
        };
    }

    /// <summary>Rebuilds the recogniser so an edited hint list takes effect.</summary>
    /// <remarks>
    /// Only worth doing under beam search, which is the only mode where hints do anything.
    /// A failure here leaves the previous recogniser gone, so the tray drops to Busy and the
    /// user is told — silently carrying on with no recogniser would look like a dead hotkey.
    /// </remarks>
    private async void ReloadRecogniserForHints()
    {
        if (_transcriber is null || _settings.Decoding != DecodingMethod.BeamSearch) return;

        try
        {
            await _transcriber.ReloadAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is TranscriberException or IOException)
        {
            Dispatch(() =>
            {
                _modelReady = false;
                SetTrayState("Teezy — model not loaded", ready: false);
                Notify($"Dictionary hints could not be applied: {e.Message}", Forms.ToolTipIcon.Warning);
            });
        }
    }
}
