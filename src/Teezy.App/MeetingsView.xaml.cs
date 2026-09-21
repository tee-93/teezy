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
using Teezy.Core.Cost;
using Teezy.Core.Meetings;
using Teezy.Documents;

namespace Teezy.App;

/// <summary>One recorded meeting, as the list shows it.</summary>
public sealed record MeetingRow(
    string Title,
    string Meta,
    bool HasTranscript,
    bool CanTranscribe,
    bool CanSummarise,
    bool HasNotes,
    bool CanDelete,
    MeetingRecord Record)
{
    public Visibility TranscriptVisibility => Show(HasTranscript);

    public Visibility TranscribeVisibility => Show(CanTranscribe);

    public Visibility SummariseVisibility => Show(CanSummarise);

    public Visibility NotesVisibility => Show(HasNotes);

    public Visibility DeleteVisibility => Show(CanDelete);

    private static Visibility Show(bool shown) => shown ? Visibility.Visible : Visibility.Collapsed;
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
/// <b>Summarising is the opposite: never automatic.</b> It is the only step that sends a
/// meeting off this computer, so it happens when the button on that meeting is pressed and at
/// no other time. It is a network call, not processor work, so it is allowed during a recording.
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
    private readonly IMeetingSummariser? _summariser;
    private readonly DispatcherTimer _tick;
    private readonly Dictionary<string, string> _failures = [];
    private readonly HashSet<string> _summarising = [];

    private CancellationTokenSource? _transcribing;
    private MeetingRecord? _working;
    private string? _startProblem;

    // Written from capture threads, read on the timer. A torn float costs one frame of meter.
    private float _meLevel;
    private float _themLevel;

    public MeetingsView(
        MeetingStore store,
        MeetingRecorder recorder,
        ITranscriber? transcriber,
        IMeetingSummariser? summariser = null)
    {
        InitializeComponent();
        _store = store;
        _recorder = recorder;
        _transcriber = transcriber;
        _summariser = summariser;

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
            ? $"Only your microphone is being recorded. Windows would not let TeezyFlow hear the speakers: {speakers}"
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
        WorkDetail.Text = $"{MeetingTranscript.Clock(progress.SpeechDone)} of {MeetingTranscript.Clock(progress.SpeechTotal)} of audio"
                          + $" · {MeetingTranscript.Clock(progress.Elapsed)} so far";
    }

    private void OnCancelTranscription(object sender, RoutedEventArgs e) => _transcribing?.Cancel();

    private void OnTranscribe(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MeetingRow row) _failures.Remove(row.Record.Folder);
        _ = TranscribePendingAsync();
    }

    // ---- summary and follow-ups ----

    private async void OnSummarise(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MeetingRow row) return;
        var meeting = row.Record;

        if (_summariser is not { IsAvailable: true } summariser)
        {
            Warn("Summaries are written by Claude, using your own Anthropic API key, and none is saved yet.\n\n"
                 + "Add one in Settings ▸ Dictation, under Smarter cleanup with Claude. The switch there can stay off.");
            return;
        }

        if (!_summarising.Add(meeting.Folder)) return;
        _failures.Remove(meeting.Folder);
        ShowList();

        try
        {
            var lines = MeetingTranscript.ParseLines(await File.ReadAllTextAsync(meeting.TranscriptPath));
            var notes = await summariser.SummariseAsync(meeting.Info, lines);

            _store.SaveNotes(meeting, notes);
            await Task.Run(() => MeetingNotesPdf.Write(meeting.PdfPath, meeting.Info, notes, lines));

            OpenFile(meeting.PdfPath);
        }
        catch (MeetingSummaryException problem)
        {
            _failures[meeting.Folder] = problem.Message;
            Warn(problem.Message);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException
                                            or InvalidOperationException)
        {
            // InvalidOperationException is PDFsharp finding no usable font, the one way the PDF
            // itself fails; the notes are already saved by then, so Notes PDF can try again.
            _failures[meeting.Folder] = problem.Message;
            Warn($"The notes could not be saved as a PDF: {problem.Message}");
        }
        finally
        {
            _summarising.Remove(meeting.Folder);
            ShowList();
        }
    }

    /// <summary>Opens the notes PDF, making it again from the saved notes if it has gone.</summary>
    private void OnNotes(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MeetingRow row) return;
        var meeting = row.Record;

        try
        {
            if (!File.Exists(meeting.PdfPath) && _store.LoadNotes(meeting) is { } notes)
            {
                var lines = meeting.HasTranscript
                    ? MeetingTranscript.ParseLines(File.ReadAllText(meeting.TranscriptPath))
                    : [];
                MeetingNotesPdf.Write(meeting.PdfPath, meeting.Info, notes, lines);
            }

            OpenFile(meeting.PdfPath);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException
                                            or InvalidOperationException)
        {
            Warn($"The notes could not be saved as a PDF: {problem.Message}");
        }
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
        var summarising = _summarising.Contains(record.Folder);
        var length = info.Recorded > TimeSpan.Zero ? MeetingTranscript.Clock(info.Recorded) : "Length unknown";

        var meta = (record.HasTranscript, info.Stats) switch
        {
            _ when summarising => $"{length} · Claude is writing the summary and follow-up tasks…",
            (true, { } stats) => string.Create(CultureInfo.CurrentCulture,
                $"{length} · transcribed in {MeetingTranscript.Clock(stats.Took)}, {stats.TimesRealtime:0.0}× realtime"),
            (true, null) => length,
            _ when working => $"{length} · transcribing now",
            _ when !record.HasAudio => "No audio and no transcript",
            _ when _recorder.IsRecording => $"{length} · waits until this recording ends",
            _ when _transcriber is not { IsLoaded: true } => $"{length} · waiting for the speech model to load",
            _ => $"{length} · not transcribed yet",
        };

        if (!summarising && record.HasNotes && _store.LoadNotes(record) is { } notes)
        {
            meta += Cost(notes);
        }

        if (!summarising && _failures.TryGetValue(record.Folder, out var failure))
        {
            meta += $" · {failure}";
        }

        return new MeetingRow(
            When(info.Started),
            meta,
            HasTranscript: record.HasTranscript,
            CanTranscribe: record.HasAudio && !record.HasTranscript && !working && _transcribing is null
                           && !_recorder.IsRecording && _transcriber is { IsLoaded: true },
            CanSummarise: record.HasTranscript && !record.HasNotes && !summarising,
            HasNotes: record.HasNotes && !summarising,
            CanDelete: !working && !summarising,
            record);
    }

    /// <summary>What the summary cost, when the price of the model is known.</summary>
    private static string Cost(SavedNotes notes) =>
        ModelRates.Cost(notes.Model, notes.Tokens, DateOnly.FromDateTime(notes.Written.LocalDateTime)) is { } dollars
            ? string.Create(CultureInfo.InvariantCulture, $" · summary cost US${dollars:0.00}")
            : " · summarised";

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is MeetingRow row) OpenFile(row.Record.TranscriptPath);
    }

    private void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception problem) when (problem is System.ComponentModel.Win32Exception or IOException)
        {
            Warn(problem.Message);
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
            Warn(problem.Message);
        }
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MeetingRow row || !row.CanDelete) return;

        var what = row.HasNotes ? "its transcript and notes" : row.HasTranscript ? "its transcript" : "its recording";
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
            Warn(problem.Message);
        }

        ShowList();
    }

    private void Warn(string message) =>
        MessageBox.Show(Window.GetWindow(this), message, "TeezyFlow", MessageBoxButton.OK, MessageBoxImage.Warning);

    private static string When(DateTimeOffset started) =>
        started.ToLocalTime().ToString("dddd d MMMM, h:mm tt", CultureInfo.CurrentCulture);
}
