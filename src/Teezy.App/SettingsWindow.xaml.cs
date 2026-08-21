using System;
using System.Linq;
using System.Diagnostics;
using System.Windows;
using Teezy.Core;
using Teezy.Core.Abstractions;
using Teezy.Core.Dictionary;
using Teezy.Speech;

namespace Teezy.App;

public partial class SettingsWindow : Window
{
    private TeezySettings _settings;

    /// <summary>Suppresses change events while the controls are being populated, so loading
    /// the window does not look like the user editing it.</summary>
    private bool _loading = true;

    public event Action<TeezySettings>? SettingsChanged;

    public SettingsWindow(TeezySettings settings, ParakeetTranscriber? transcriber, bool modelReady)
    {
        InitializeComponent();
        _settings = settings;

        Subtitle.Text = "On-device dictation. Nothing leaves this machine.";

        foreach (var key in Enum.GetValues<PushToTalkKey>())
        {
            KeyPicker.Items.Add(App.KeyName(key));
        }
        KeyPicker.SelectedIndex = Array.IndexOf(Enum.GetValues<PushToTalkKey>(), settings.PushToTalkKey);

        foreach (var n in new[] { 1, 2, 4, 6, 8 }) ThreadPicker.Items.Add(n);
        ThreadPicker.SelectedItem = settings.NumThreads;

        CleanupBox.IsChecked = settings.CleanupEnabled;
        HudBox.IsChecked = settings.ShowHud;
        SoundBox.IsChecked = settings.SoundEnabled;

        if (modelReady && transcriber?.Paths is { } paths)
        {
            ModelStatus.Text =
                $"Parakeet TDT 0.6B v2 · loaded in {transcriber.LoadTime.TotalSeconds:F1} s";
            ModelPath.Text = paths.Directory;
        }
        else
        {
            ModelStatus.Text = "Not loaded.";
            ModelPath.Text = ModelPaths.DefaultDirectory;
        }

        _loading = false;
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings = _settings with
        {
            PushToTalkKey = Enum.GetValues<PushToTalkKey>()[Math.Max(0, KeyPicker.SelectedIndex)],
            NumThreads = ThreadPicker.SelectedItem as int? ?? _settings.NumThreads,
            CleanupEnabled = CleanupBox.IsChecked == true,
            ShowHud = HudBox.IsChecked == true,
            SoundEnabled = SoundBox.IsChecked == true,
        };

        SettingsChanged?.Invoke(_settings);
    }

    private void OnEditDictionary(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(DictionaryStore.DefaultPath) { UseShellExecute = true });

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
