using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Teezy.Core.Abstractions;
using Teezy.Core.Meetings;

namespace Teezy.App;

/// <summary>One recorded meeting, as the list shows it.</summary>
public sealed record MeetingRow(string Title, string Meta, bool HasTranscript, bool CanTranscribe, bool CanDelete, MeetingRecord Record)
{
    public Visibility TranscriptVisibility => HasTranscript ? Visibility.Visible : Visibility.Collapsed;

    public Visibility TranscribeVisibility => CanTranscribe ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DeleteVisibility => CanDelete ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Records meetings, transcribes them afterwards, and lists what came of it.</summary>
/// <remarks>
/// <para>
/// <b>Transcription waits for recording to end, and gives way when a new one starts.</b> On
/// the work laptop the speech model and a Teams call would be fighting over two performance
/// cores. So stopping a recording starts working through every meeting still waiting — which
/// handles back-to-back days on its own — and starting one stops that work. A stopped
/// transcription keeps its audio and starts over next time; nothing is lost but the time.
/// </para>
/// <para>
/// The recorder is owned by the app, not this page, so a recording carries on with the
/// window closed and is saved properly if Teezy quits. This page only ever looks at it.
/// </para>
/// </remarks>
public partial class MeetingsView : UserControl
{
    private readonly MeetingStore _store;
    private readonly MeetingRecorder _recorder;
    private readonly ITranscriber? _transcriber;
    private readonly DispatcherTimer _tick;
    private readonly Dictionary<string, string> _failures = [];

    private CancellationTokenSource? _transcribing;
    private MeetingRecord? _working;
    private string? _startProblem;

    // Written from capture threads, read on the timer. A torn float costs one frame of meter.
    private float _meLevel;
    private float _themLevel;

    public MeetingsView(MeetingStore store, MeetingRecorder recorder, ITranscriber? transcriber)
    {
        InitializeComponent();
        _store = store;
        _recorder = recorder;
        _transcriber = transcriber;

        _recorder.LevelChanged += OnLevel;

        _tick = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(80) };
        _tick.Tick += OnTick;

        Refresh();
    }

    public void Refresh()
    {
        ShowRecorder();
        ShowWork(null);
        ShowList();
    }

    // ---- recording ----

    private void OnRecord(object sender, RoutedEventArgs e)
    {
        _startProblem = null;

        if (_recorder.IsRecording)
        {
            _recorder.Stop();
            _tick.Stop();
            ShowRecorder();
            ShowList();
            _ = TranscribePendingAsync();
            return;
        }

        // The call gets the processor. Whatever was transcribing picks up again afterwards.
        _transcribing?.Cancel();

        try
        {
            _recorder.Start();
            _tick.Start();
        }
        catch (AudioCaptureException problem)
        {
            _startProblem = problem.Message;
        }

        ShowRecorder();
        ShowList();
    }

    private void OnLevel(Side side, float level)
    {
        if (side == Side.Me) _meLevel = Math.Max(_meLevel, level);
        else _themLevel = Math.Max(_themLevel, level);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        ElapsedText.Text = MeetingTranscript.Clock(_recorder.Elapsed);

        MeFill.Width = Math.Clamp(_meLevel, 0, 1) * MeTrack.ActualWidth;
        ThemFill.Width = Math.Clamp(_themLevel, 0, 1) * ThemTrack.ActualWidth;

        // Loopback delivers nothing at all while nobody talks, so without decay the meter
        // would freeze at the last word instead of falling back to rest.
        _meLevel *= 0.6f;
        _themLevel *= 0.6f;
    }

    private void ShowRecorder()
    {
        var recording = _recorder.IsRecording;

        RecordButton.Content = recording ? "Stop and transcribe" : "Start recording";
        RecorderTitle.Text = recording ? "Recording" : "Record a meeting";
        RecordingClock.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        Meters.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;

        if (recording)
        {
            ElapsedText.Text = MeetingTranscript.Clock(_recorder.Elapsed);
            MeName.Text = _recorder.MicrophoneName ?? "";
            ThemName.Text = _recorder.SpeakersName ?? "not being recorded";
        }

        var problem = _startProblem ?? (recording && _recorder.SpeakersProblem is { } speakers
            ? $"Only your microphone is being recorded. Windows would not let Teezy hear the speakers: {speakers}"
            : null);

        ProblemText.Text = problem ?? "";
        ProblemPanel.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- transcribing ----

    /// <summary>Works through every meeting that still has audio and no transcript, oldest first.</summary>
    private async Task TranscribePendingAsync()
    {
        if (_transcribing is not null || _recorder.IsRecording) return;

        if (_transcriber is not { IsLoaded: true } model)
        {
            ShowList();
            return;
        }

        using var cancel = new CancellationTokenSource();
        _transcribing = cancel;

        try
        {
            while (!cancel.IsCancellationRequested && NextWaiting() is { } next)
            {
                _working = next;
                _failures.Remove(next.Folder);
                ShowWork(null);
                ShowList();

                try
                {
                    await new MeetingTranscriber(model, _store).TranscribeAsync(
                        next, new Progress<MeetingProgress>(ShowWork), cancel.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception problem) when (problem is IOException or InvalidDataException
                                                    or UnauthorizedAccessException or TranscriberException)
                {
                    // Remembered so the loop moves on rather than retrying the same broken file forever.
                    _failures[next.Folder] = problem.Message;
                }
            }
        }
        finally
        {
            _transcribing = null;
            _working = null;
            ShowWork(null);
            ShowList();
        }
    }

    private MeetingRecord? NextWaiting() => _store.List()
        .Where(r => r.HasAudio && !r.HasTranscript
                    && r.Folder != _recorder.Current?.Folder
                    && !_failures.ContainsKey(r.Folder))
        .OrderBy(r => r.Info.Started)
        .FirstOrDefault();

    private void ShowWork(MeetingProgress? progress)
    {
        if (_working is null)
        {
            WorkCard.Visibility = Visibility.Collapsed;
            return;
        }

        WorkCard.Visibility = Visibility.Visible;
        WorkTitle.Text = $"Transcribing the meeting from {When(_working.Info.Started)}";

        if (progress is null)
        {
            WorkFill.Width = 0;
            WorkDetail.Text = "Finding where people spoke…";
            return;
        }

        WorkFill.Width = Math.Clamp(progress.Fraction, 0, 1) * WorkTrack.ActualWidth;
        WorkDetail.Text = $"{MeetingTranscript.Clock(progress.SpeechDone)} of {MeetingTranscript.Clock(progress.SpeechTotal)} of speech"
                          + $" · {MeetingTranscript.Clock(progress.Elapsed)} so far";
    }

    private void OnCancelTranscription(object sender, RoutedEventArgs e) => _transcribing?.Cancel();

    private void OnTranscribe(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MeetingRow row) _failures.Remove(row.Record.Folder);
        _ = TranscribePendingAsync();
    }

    // ---- the list ----

    private void ShowList()
    {
        var rows = _store.List()
            .Where(r => r.Folder != _recorder.Current?.Folder)
            .Select(Row)
            .ToList();

        MeetingList.ItemsSource = rows;
        ListCard.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private MeetingRow Row(MeetingRecord record)
    {
        var info = record.Info;
        var working = record.Folder == _working?.Folder;
        var length = info.Recorded > TimeSpan.Zero ? MeetingTranscript.Clock(info.Recorded) : "Length unknown";

        var meta = (record.HasTranscript, info.Stats) switch
        {
            (true, { } stats) => string.Create(CultureInfo.CurrentCulture,
                $"{length} · transcribed in {MeetingTranscript.Clock(stats.Took)}, {stats.TimesRealtime:0.0}× realtime"),
            (true, null) => length,
            _ when working => $"{length} · transcribing now",
            _ when _failures.TryGetValue(record.Folder, out var failure) => $"{length} · could not be transcribed: {failure}",
            _ when !record.HasAudio => "No audio and no transcript",
            _ when _recorder.IsRecording => $"{length} · waits until this recording ends",
            _ when _transcriber is not { IsLoaded: true } => $"{length} · waiting for the speech model to load",
            _ => $"{length} · not transcribed yet",
        };

        return new MeetingRow(
            When(info.Started),
            meta,
            HasTranscript: record.HasTranscript,
            CanTranscribe: record.HasAudio && !record.HasTranscript && !working && _transcribing is null
                           && !_recorder.IsRecording && _transcriber is { IsLoaded: true },
            CanDelete: !working,
            record);
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MeetingRow row) return;

        try
        {
            Process.Start(new ProcessStartInfo(row.Record.TranscriptPath) { UseShellExecute = true });
        }
        catch (Exception problem) when (problem is System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show(Window.GetWindow(this), problem.Message, "Teezy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MeetingRow row) return;

        try
        {
            Clipboard.SetText(File.ReadAllText(row.Record.TranscriptPath));
        }
        catch (Exception problem) when (problem is IOException or System.Runtime.InteropServices.COMException)
        {
            MessageBox.Show(Window.GetWindow(this), problem.Message, "Teezy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MeetingRow row || row.Record.Folder == _working?.Folder) return;

        var what = row.HasTranscript ? "its transcript" : "its recording";
        var answer = MessageBox.Show(
            Window.GetWindow(this),
            $"Delete the meeting from {row.Title} and {what}? This cannot be undone.",
            "Delete meeting", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _store.Delete(row.Record);
        }
        catch (IOException problem)
        {
            MessageBox.Show(Window.GetWindow(this), problem.Message, "Teezy", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        ShowList();
    }

    private static string When(DateTimeOffset started) =>
        started.ToLocalTime().ToString("dddd d MMMM, h:mm tt", CultureInfo.CurrentCulture);
}
