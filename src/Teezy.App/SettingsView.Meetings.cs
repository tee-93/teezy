using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Windows;
using Teezy.Core;
using Teezy.Core.Abstractions;
using Teezy.Speech;

namespace Teezy.App;

/// <summary>
/// Settings ▸ Audio ▸ Meetings: which output the other people are recorded from, whether their
/// voices are told apart, and how long the recording is kept.
/// </summary>
public partial class SettingsView
{
    private readonly DiariserModels _speakerModels = DiariserModels.Default;
    private CancellationTokenSource? _speakerDownload;

    /// <summary>How long a recording can be kept for, and what the rows say.</summary>
    private static readonly (int Days, string Label)[] KeepChoices =
    [
        (0, "Delete it once transcribed"),
        (1, "Keep for a day"),
        (7, "Keep for a week"),
        (30, "Keep for a month"),
    ];

    private sealed record OutputChoice(string? Id, string? Name, string Label);

    private void PopulateMeetings(TeezySettings settings)
    {
        PopulateOutputs(settings);

        var index = Array.FindIndex(KeepChoices, c => c.Days == settings.KeepMeetingAudioDays);
        KeepAudioPicker.Items.Clear();
        foreach (var (_, label) in KeepChoices) KeepAudioPicker.Items.Add(label);
        KeepAudioPicker.SelectedIndex = index >= 0 ? index : 0;

        SpeakersSwitch.IsChecked = settings.TellSpeakersApart;
        ShowSpeakerModels();
    }

    private void PopulateOutputs(TeezySettings settings)
    {
        IReadOnlyList<AudioDevice> devices = [];
        try
        {
            // Asking what is plugged in does not open anything; disposed immediately.
            using var probe = new Teezy.Platform.Windows.WindowsAudioCapture(
                Teezy.Platform.Windows.CaptureSource.Speakers);
            devices = probe.Devices();
        }
        catch (AudioCaptureException)
        {
            // No sound hardware to list. The row stays, saying so.
        }

        var rows = new List<OutputChoice>
        {
            new(null, null, devices.FirstOrDefault(d => d.IsSystemDefault) is { } d
                ? $"Whatever Windows is playing to — {d.Name}"
                : "Whatever Windows is playing to"),
        };

        rows.AddRange(devices.Select(device => new OutputChoice(device.Id, device.Name, device.Name)));

        // A chosen output that is unplugged today keeps its row, so the choice is not silently
        // reset the first time the headset is left at home.
        if (settings.MeetingOutputId is { Length: > 0 } chosen && rows.All(r => r.Id != chosen))
        {
            rows.Add(new OutputChoice(
                chosen, settings.MeetingOutputName,
                $"{settings.MeetingOutputName ?? "Chosen output"} — not connected"));
        }

        OutputPicker.Items.Clear();
        foreach (var row in rows) OutputPicker.Items.Add(row.Label);
        OutputPicker.Tag = rows;

        var index = rows.FindIndex(r => r.Id == settings.MeetingOutputId);
        OutputPicker.SelectedIndex = index >= 0 ? index : 0;
        OutputPicker.IsEnabled = devices.Count > 0;
    }

    private void OnMeetingOutputChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || OutputPicker.Tag is not List<OutputChoice> rows) return;
        if (OutputPicker.SelectedIndex < 0 || OutputPicker.SelectedIndex >= rows.Count) return;

        var chosen = rows[OutputPicker.SelectedIndex];
        _write(_read() with { MeetingOutputId = chosen.Id, MeetingOutputName = chosen.Name });
    }

    private void OnKeepMeetingAudioChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || KeepAudioPicker.SelectedIndex < 0) return;
        _write(_read() with { KeepMeetingAudioDays = KeepChoices[KeepAudioPicker.SelectedIndex].Days });
    }

    private void OnTellSpeakersApart(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var wanted = SpeakersSwitch.IsChecked == true;
        _write(_read() with { TellSpeakersApart = wanted });

        // Asking for it without the models is the clearest moment to offer them.
        if (wanted && !_speakerModels.Ready && _speakerDownload is null) OnDownloadSpeakerModels(sender, e);
        else ShowSpeakerModels();
    }

    private void ShowSpeakerModels()
    {
        var downloading = _speakerDownload is not null;
        var ready = _speakerModels.Ready;

        SpeakersDownloadButton.Visibility = !ready && !downloading ? Visibility.Visible : Visibility.Collapsed;
        SpeakersSwitch.IsEnabled = !downloading;

        if (downloading) return;

        SpeakersStatus.Text = ready
            ? "Each person at the far end is labelled Speaker 1, Speaker 2 and so on, and you can rename them on the Meetings page. Worked out on this computer."
            : $"A one-off download of about {DiariserModels.DownloadBytes / 1_000_000} MB. Until then everyone but you is simply “Them”.";
    }

    private async void OnDownloadSpeakerModels(object sender, RoutedEventArgs e)
    {
        if (_speakerDownload is not null) return;

        _speakerDownload = new CancellationTokenSource();
        ShowSpeakerModels();
        SpeakersStatus.Text = "Starting the download…";

        var progress = new Progress<ModelDownloadProgress>(p =>
            SpeakersStatus.Text = $"Downloading — {p.OverallFraction:P0} ({p.FileName})");

        try
        {
            await new ModelDownloader().FetchAsync(
                _speakerModels.Directory, DiariserModels.Downloads, progress, _speakerDownload.Token);

            _speakerDownload = null;
            ShowSpeakerModels();
        }
        catch (Exception problem) when (problem is HttpRequestException or IOException
                                            or OperationCanceledException or UnauthorizedAccessException)
        {
            var cancelled = problem is OperationCanceledException;
            _speakerDownload = null;
            ShowSpeakerModels();
            SpeakersStatus.Text = cancelled
                ? "Cancelled. Download again to pick up where it stopped."
                : $"The download stopped: {problem.Message} Download again to pick up where it stopped.";
        }
    }
}
