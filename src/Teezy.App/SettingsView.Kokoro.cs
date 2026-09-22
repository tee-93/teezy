using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows;
using Teezy.Core;
using Teezy.Core.Abstractions;
using Teezy.Core.Voice;
using Teezy.Speech;

namespace Teezy.App;

/// <summary>Settings ▸ Assistant ▸ the natural (Kokoro) voice: its download and its voices.</summary>
public partial class SettingsView
{
    private readonly KokoroModel _kokoro = new();
    private CancellationTokenSource? _kokoroDownload;

    private void ShowKokoroState()
    {
        var downloading = _kokoroDownload is not null;
        var installed = _kokoro.IsInstalled;

        KokoroDownloadButton.Visibility = !installed && !downloading ? Visibility.Visible : Visibility.Collapsed;
        KokoroCancelButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        KokoroRemoveButton.Visibility = installed && !downloading ? Visibility.Visible : Visibility.Collapsed;

        if (!downloading)
        {
            KokoroStatus.Text = installed
                ? "Installed. Made on this computer — nothing you hear is sent anywhere."
                : $"A one-off download of about {KokoroModel.ApproximateBytes / 1_000_000} MB from Hugging Face. Until then, answers use the Windows voice.";
        }
    }

    private void PopulateKokoroVoices(TeezySettings settings, IReadOnlyList<SpeechVoice> voices)
    {
        var rows = new List<VoiceChoice>();
        if (voices.Count > 0)
        {
            rows.Add(new VoiceChoice(null, $"Automatic — {KokoroVoices.Default.Name} (British)"));
            rows.AddRange(voices.Select(v => new VoiceChoice(
                v.Id, $"{v.Name} — {(v.Culture == "en-GB" ? "British" : "American")}, {v.Gender.ToLowerInvariant()}")));
        }

        VoicePicker.Items.Clear();
        foreach (var row in rows) VoicePicker.Items.Add(row.Label);
        VoicePicker.Tag = rows;

        var index = rows.FindIndex(r => r.Id == settings.KokoroVoice);
        VoicePicker.SelectedIndex = rows.Count == 0 ? -1 : index >= 0 ? index : 0;
        VoicePicker.IsEnabled = rows.Count > 0;
        VoiceTestButton.IsEnabled = rows.Count > 0;

        VoiceHint.Text = rows.Count == 0
            ? "Download the natural voices above to choose one."
            : "British voices first — the nearest Kokoro has to Australian. Each is made on this computer, free.";
    }

    private async void OnDownloadKokoro(object sender, RoutedEventArgs e)
    {
        if (_kokoroDownload is not null) return;
        _kokoroDownload = new CancellationTokenSource();
        ShowKokoroState();
        KokoroStatus.Text = "Starting the download…";

        var progress = new Progress<ModelDownloadProgress>(p =>
            KokoroStatus.Text = $"Downloading — {p.OverallFraction:P0} ({p.FileName})");

        try
        {
            await _kokoro.DownloadAsync(progress, _kokoroDownload.Token);
            _kokoroDownload = null;
            Refresh();

            // Heard at once: the point of downloading it was what it sounds like.
            Speak();
        }
        catch (Exception problem) when (problem is HttpRequestException or IOException
                                            or OperationCanceledException or UnauthorizedAccessException
                                            or System.Text.Json.JsonException)
        {
            var cancelled = problem is OperationCanceledException;
            _kokoroDownload = null;
            ShowKokoroState();
            KokoroStatus.Text = cancelled
                ? "Cancelled. Download again to pick up where it stopped."
                : $"The download stopped: {problem.Message} Download again to pick up where it stopped.";
        }
    }

    private void OnCancelKokoro(object sender, RoutedEventArgs e) => _kokoroDownload?.Cancel();

    private void OnRemoveKokoro(object sender, RoutedEventArgs e)
    {
        // Two presses, so a stray click does not throw away 170 MB.
        if (KokoroRemoveButton.Tag is not "armed")
        {
            KokoroRemoveButton.Tag = "armed";
            KokoroRemoveButton.Content = "Press again to remove";
            return;
        }

        KokoroRemoveButton.Tag = null;
        KokoroRemoveButton.Content = "Remove";
        _speaker?.Stop();

        try
        {
            _kokoro.Delete();
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
        {
            // The model is open while it is in use; the next start frees it.
            KokoroStatus.Text = $"Couldn’t remove it while it is in use. Restart TeezyFlow and try again. ({problem.Message})";
            return;
        }

        Refresh();
    }
}
