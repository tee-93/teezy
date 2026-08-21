using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Wisper.Core;
using Wisper.Core.Abstractions;
using Wisper.Core.Dictionary;
using Wisper.Platform.Windows;
using Wisper.Speech;
using Forms = System.Windows.Forms;

namespace Wisper.App;

public partial class App : Application
{
    private DictationController? _controller;
    private ParakeetTranscriber? _transcriber;
    private HudWindow? _hud;
    private Forms.NotifyIcon? _tray;
    private DictionaryStore? _dictionary;
    private WisperSettings _settings = new();
    private bool _modelReady;
    private FileSystemWatcher? _dictWatcher;
    private SingleInstance? _instance;
    private Forms.ToolStripMenuItem? _downloadItem;

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
                Wisper is already running.

                Look for the microphone icon in the system tray — click the ^ arrow
                next to the clock if you cannot see it.
                """,
                "Wisper", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _settings = WisperSettings.Load();
        _dictionary = new DictionaryStore(DictionaryStore.DefaultPath);
        EnsureDictionaryFileExists();
        WatchDictionaryFile();

        _hud = new HudWindow();
        BuildTray();

        _transcriber = new ParakeetTranscriber(_settings.ModelPath, _settings.NumThreads);

        _controller = new DictationController(
            new WindowsHotkeySource(),
            new WindowsAudioCapture(),
            _transcriber,
            new WindowsTextInjector(),
            _dictionary,
            () => _settings);

        // Every one of these fires on a background thread. WPF objects may only be touched
        // from the UI thread, so each hops the dispatcher rather than assuming.
        _controller.StateChanged += s => Dispatch(() => OnStateChanged(s));
        _controller.LevelChanged += l => Dispatch(() => _hud!.SetLevel(l));
        _controller.Failed += m => Dispatch(() => OnFailed(m));
        _controller.Completed += OnCompleted;

        // The hook must be installed from a thread with a message pump; OnStartup is on the
        // UI thread, which has one. From a pool thread the callback silently never fires.
        if (!_controller.Start())
        {
            Notify("Wisper could not install its keyboard hook.", Forms.ToolTipIcon.Error);
        }

        await LoadModelAsync().ConfigureAwait(false);
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
                $"Ready — hold {KeyName(_settings.PushToTalkKey)} to dictate", ready: true));
            Debug.WriteLine($"model loaded in {sw.ElapsedMilliseconds} ms");
        }
        catch (TranscriberException ex)
        {
            Dispatch(() =>
            {
                SetTrayState("Speech model not found", ready: false);
                MessageBox.Show(ex.Message, "Wisper — model problem",
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

        if (result.Injection == InjectionResult.Failed)
        {
            Dispatch(() => Notify(
                "Couldn't type into that window. Elevated apps need Wisper to be elevated too.",
                Forms.ToolTipIcon.Warning));
        }
    }

    private void OnFailed(string message)
    {
        _hud!.ShowState(DictationState.Error, message);
        Notify(message, Forms.ToolTipIcon.Error);
    }

    private void Dispatch(Action action) => Dispatcher.BeginInvoke(action, DispatcherPriority.Normal);

    // ---- Tray ----

    private void BuildTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Icon = TrayIcons.Idle,
            Visible = true,
            Text = "Wisper — starting…",
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Settings…", null, (_, _) => ShowSettings());
        menu.Items.Add("Edit dictionary…", null, (_, _) => OpenDictionaryFile());

        // Only meaningful when setup was cancelled or failed, so it hides itself once the
        // model is loaded rather than sitting in the menu as a permanent puzzle.
        _downloadItem = new Forms.ToolStripMenuItem("Download speech model…", null,
            async (_, _) => await LoadModelAsync());
        menu.Items.Add(_downloadItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Wisper", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowSettings();
    }

    private void SetTrayState(string text, bool ready)
    {
        if (_tray is null) return;
        // NotifyIcon.Text is capped at 63 characters and throws above it.
        _tray.Text = text.Length > 63 ? text[..63] : text;
        if (_downloadItem is not null) _downloadItem.Visible = !ready;
        _tray.Icon = ready ? TrayIcons.Idle : TrayIcons.Loading;
    }

    private void Notify(string message, Forms.ToolTipIcon icon) =>
        _tray?.ShowBalloonTip(4000, "Wisper", message, icon);

    private void ShowSettings()
    {
        var existing = Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing is not null) { existing.Activate(); return; }

        var window = new SettingsWindow(_settings, _transcriber, _modelReady);
        window.SettingsChanged += updated =>
        {
            var keyChanged = updated.PushToTalkKey != _settings.PushToTalkKey;
            _settings = updated;
            _settings.Save();
            if (keyChanged) _controller?.ReloadHotkey();
            SetTrayState($"Ready — hold {KeyName(_settings.PushToTalkKey)} to dictate", _modelReady);
        };
        window.Show();
    }

    private void EnsureDictionaryFileExists()
    {
        var path = DictionaryStore.DefaultPath;
        if (File.Exists(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, DictionaryStore.SampleFile);
    }

    private void OpenDictionaryFile()
    {
        EnsureDictionaryFileExists();
        Process.Start(new ProcessStartInfo(DictionaryStore.DefaultPath) { UseShellExecute = true });
    }

    internal static string KeyName(PushToTalkKey key) => key switch
    {
        PushToTalkKey.RightControl => "Right Ctrl",
        PushToTalkKey.RightShift => "Right Shift",
        PushToTalkKey.ScrollLock => "Scroll Lock",
        PushToTalkKey.Pause => "Pause",
        PushToTalkKey.F13 => "F13",
        _ => key.ToString(),
    };

    protected override void OnExit(ExitEventArgs e)
    {
        _dictWatcher?.Dispose();
        _instance?.Dispose();
        _controller?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        TrayIcons.Dispose();
        base.OnExit(e);
    }
}
