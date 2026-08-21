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
using Teezy.Platform.Windows;
using Teezy.Speech;
using Forms = System.Windows.Forms;

namespace Teezy.App;

public partial class App : Application
{
    /// <summary>Name the API key is filed under in the encrypted secret store.</summary>
    internal const string ApiKeyName = "anthropic-api-key";

    private DictationController? _controller;
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

                Look for the microphone icon in the system tray — click the ^ arrow
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
        BuildTray();

        _transcriber = new ParakeetTranscriber(_settings.ModelPath, _settings.NumThreads);

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
            TimeSpan.FromSeconds(Math.Clamp(_settings.LlmTimeoutSeconds, 2, 30)));

        _controller = new DictationController(
            _hotkeySource,
            new WindowsAudioCapture(),
            _transcriber,
            new WindowsTextInjector(),
            _dictionary,
            () => _settings,
            new WindowsForegroundApp(),
            () => _settings.LlmCleanupEnabled ? _claude! : new RuleBasedFormatter());

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
            Notify("Teezy could not install its keyboard hook.", Forms.ToolTipIcon.Error);
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
            _claude);

        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.ShowPage(page);
        _main.RefreshCurrentPage();
    }

    /// <summary>Applies and persists a settings change from any window.</summary>
    private void ApplySettings(TeezySettings updated)
    {
        var keyChanged = updated.Hotkey != _settings.Hotkey;
        _settings = updated;
        _settings.Save();
        if (keyChanged) _controller?.ReloadHotkey();
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
        _controller?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        TrayIcons.Dispose();
        base.OnExit(e);
    }
}
