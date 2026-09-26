using System.Diagnostics;
using Teezy.Core.Abstractions;

namespace Teezy.Core.Meetings;

/// <summary>How far through a transcription is, in speech rather than in pieces.</summary>
/// <param name="Stage">What it is doing now, for the page to say. Null while transcribing.</param>
public sealed record MeetingProgress(
    TimeSpan SpeechDone, TimeSpan SpeechTotal, TimeSpan Elapsed, string? Stage = null)
{
    public double Fraction => SpeechTotal > TimeSpan.Zero ? SpeechDone / SpeechTotal : 1;
}

/// <summary>Transcribes a recorded meeting on this computer, after the fact.</summary>
/// <remarks>
/// <para>
/// Uses the same loaded model as dictation, through the same one-at-a-time gate. A piece of a
/// meeting can take several seconds on a slow laptop, so a dictation made while a meeting is
/// transcribing waits its turn behind at most one piece rather than behind the meeting.
/// </para>
/// <para>
/// Pieces from both sides are transcribed in the order they happened, not one file then the
/// other, so progress means "this far into the meeting" and a transcription stopped halfway
/// has done the first half of both.
/// </para>
/// </remarks>
/// <param name="keepAudio">
/// How long to leave the recording on disk after its transcript is written. Zero — the default,
/// and what the privacy policy promises unless it is changed — deletes it there and then.
/// </param>
public sealed class MeetingTranscriber(
    ITranscriber transcriber,
    MeetingStore store,
    Func<TimeSpan>? clock = null,
    IDiariser? diariser = null,
    TimeSpan keepAudio = default)
{
    private static readonly long Origin = Stopwatch.GetTimestamp();

    private readonly Func<TimeSpan> _clock = clock ?? (() => Stopwatch.GetElapsedTime(Origin));

    /// <summary>Transcribes, writes the transcript, and deletes the audio once that has succeeded.</summary>
    public async Task<MeetingRecord> TranscribeAsync(
        MeetingRecord record,
        IProgress<MeetingProgress>? progress = null,
        CancellationToken ct = default)
    {
        var begun = _clock();
        var readers = new Dictionary<Side, WavReader>();

        try
        {
            var work = new List<(Side Side, SpeechSpan Span)>();
            var levels = new Dictionary<Side, float[]>();
            long longest = 0;

            foreach (var (side, path) in new[] { (Side.Me, record.MePath), (Side.Them, record.ThemPath) })
            {
                if (!File.Exists(path)) continue;

                var reader = new WavReader(path);
                readers[side] = reader;
                longest = Math.Max(longest, reader.SampleCount);
                levels[side] = await Task.Run(() => SpeechCutter.FrameLevels(reader), ct).ConfigureAwait(false);
            }

            // What the microphone only overheard from the speakers, found in the sound and left
            // out of the pieces, so the model is never asked to transcribe the echo at all.
            var echo = EchoPath.None;
            bool[]? overheard = null;
            if (levels.TryGetValue(Side.Me, out var mine) && levels.TryGetValue(Side.Them, out var theirs))
            {
                echo = EchoGate.Measure(mine, theirs);
                if (echo.Found) overheard = EchoGate.EchoFrames(mine, theirs, echo);
            }

            foreach (var (side, reader) in readers)
            {
                var mask = side == Side.Me ? overheard : null;
                work.AddRange(SpeechCutter.Cut(levels[side], reader.SampleCount, mask).Select(span => (side, span)));
            }

            work.Sort((a, b) => a.Span.StartSample.CompareTo(b.Span.StartSample));

            var total = work.Aggregate(TimeSpan.Zero, (sum, w) => sum + w.Span.Duration);
            var done = TimeSpan.Zero;
            var lines = new List<MeetingLine>();

            progress?.Report(new MeetingProgress(done, total, _clock() - begun));

            foreach (var (side, span) in work)
            {
                ct.ThrowIfCancellationRequested();

                var samples = readers[side].Read(span.StartSample, (int)span.Length);
                var text = await transcriber.TranscribeAsync(samples, ct).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    lines.Add(new MeetingLine(span.Start, span.Duration, side, text.Trim()));
                }

                done += span.Duration;
                progress?.Report(new MeetingProgress(done, total, _clock() - begun));
            }

            // Still a second net for whatever the gate let through: a quieter echo than the
            // room's usual, or one arriving while the gate had nothing to measure from.
            var kept = MeetingTranscript.DropEchoes(lines, out var echoes, acoustic: echo.Found);

            // Who at the far end said what. Last, and only if the models are there: it costs
            // roughly a fifth of the recording's own length, and a transcript without names is
            // still a transcript.
            var voices = 0;
            if (diariser is { IsAvailable: true } && File.Exists(record.ThemPath))
            {
                progress?.Report(new MeetingProgress(done, total, _clock() - begun, "Telling the voices apart"));
                var spans = SpeakerSpans.Smooth(await diariser.SplitAsync(record.ThemPath, ct).ConfigureAwait(false));
                kept = Label(kept, spans);
                voices = spans.Select(s => s.Speaker).Distinct().Count();
            }

            var transcript = MeetingTranscript.Merge(kept);

            var measured = TimeSpan.FromSeconds((double)longest / AudioChunk.SampleRate);
            var recorded = record.Info.Recorded > measured ? record.Info.Recorded : measured;
            var stats = new TranscriptionStats(recorded, total, _clock() - begun, work.Count, echoes)
            {
                EchoDelay = echo.Found ? echo.Delay : null,
                EchoMuted = overheard is null ? TimeSpan.Zero : EchoGate.Muted(overheard),
                Voices = voices,
            };

            await File.WriteAllTextAsync(
                record.TranscriptPath,
                MeetingTranscript.Render(record.Info.Started, transcript, stats, Notes(record.Info)),
                ct).ConfigureAwait(false);

            var finished = record with { Info = record.Info with { Recorded = recorded, Stats = stats } };
            store.Save(finished);

            foreach (var reader in readers.Values) reader.Dispose();
            readers.Clear();
            if (keepAudio <= TimeSpan.Zero) store.DeleteAudio(finished);

            return finished;
        }
        finally
        {
            foreach (var reader in readers.Values) reader.Dispose();
        }
    }

    /// <summary>Puts a voice's name on each line of the far end's half of the transcript.</summary>
    /// <remarks>
    /// Numbered in the order they first say something, not in the order the model happened to
    /// group them: a transcript that opens with "Speaker 4" invites the question of who the
    /// other three were. Voices that never win a line are never numbered at all.
    /// </remarks>
    private static IReadOnlyList<MeetingLine> Label(
        IReadOnlyList<MeetingLine> lines, IReadOnlyList<SpeakerSpan> spans)
    {
        var order = new Dictionary<int, int>();
        var labelled = new List<MeetingLine>(lines.Count);

        foreach (var line in lines)
        {
            if (line.Side != Side.Them || SpeakerSpans.At(spans, line.At, line.End) is not { } speaker)
            {
                labelled.Add(line);
                continue;
            }

            if (!order.TryGetValue(speaker, out var number))
            {
                number = order.Count;
                order[speaker] = number;
            }

            labelled.Add(line with { Speaker = SpeakerSpans.Name(number) });
        }

        return labelled;
    }

    /// <summary>What the transcript should say about how it was recorded.</summary>
    private static IEnumerable<string> Notes(MeetingInfo info)
    {
        var devices = string.Join(" · ", new[]
        {
            info.MeDevice is { } me ? $"Microphone: {me}" : null,
            info.ThemDevice is { } them ? $"Speakers: {them}" : null,
        }.OfType<string>());

        if (devices.Length > 0) yield return devices;

        if (info.ThemProblem is { } problem)
        {
            yield return $"Only your microphone was recorded. Windows would not let TeezyFlow hear the speakers: {problem}";
        }

        // Only a recording that stopped cleanly knows whether anything was heard; one that was
        // interrupted never got to say, and must not be accused of silence.
        if (info.Recorded <= TimeSpan.Zero) yield break;

        if (info.ThemProblem is null && !info.ThemHeard)
        {
            yield return "Nothing at all came from the speakers. If other people were talking, "
                         + "something on this computer is stopping TeezyFlow from hearing them.";
        }

        if (!info.MeHeard)
        {
            yield return "Nothing at all came from the microphone. Check Windows' microphone privacy settings.";
        }
    }
}
