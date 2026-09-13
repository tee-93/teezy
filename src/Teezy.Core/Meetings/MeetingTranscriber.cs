using System.Diagnostics;
using Teezy.Core.Abstractions;

namespace Teezy.Core.Meetings;

/// <summary>How far through a transcription is, in speech rather than in pieces.</summary>
public sealed record MeetingProgress(TimeSpan SpeechDone, TimeSpan SpeechTotal, TimeSpan Elapsed)
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
public sealed class MeetingTranscriber(ITranscriber transcriber, MeetingStore store, Func<TimeSpan>? clock = null)
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
            long longest = 0;

            foreach (var (side, path) in new[] { (Side.Me, record.MePath), (Side.Them, record.ThemPath) })
            {
                if (!File.Exists(path)) continue;

                var reader = new WavReader(path);
                readers[side] = reader;
                longest = Math.Max(longest, reader.SampleCount);

                var levels = await Task.Run(() => SpeechCutter.FrameLevels(reader), ct).ConfigureAwait(false);
                work.AddRange(SpeechCutter.Cut(levels, reader.SampleCount).Select(span => (side, span)));
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

            var kept = MeetingTranscript.DropEchoes(lines, out var echoes);
            var transcript = MeetingTranscript.Merge(kept);

            var measured = TimeSpan.FromSeconds((double)longest / AudioChunk.SampleRate);
            var recorded = record.Info.Recorded > measured ? record.Info.Recorded : measured;
            var stats = new TranscriptionStats(recorded, total, _clock() - begun, work.Count, echoes);

            await File.WriteAllTextAsync(
                record.TranscriptPath,
                MeetingTranscript.Render(record.Info.Started, transcript, stats, Notes(record.Info)),
                ct).ConfigureAwait(false);

            var finished = record with { Info = record.Info with { Recorded = recorded, Stats = stats } };
            store.Save(finished);

            foreach (var reader in readers.Values) reader.Dispose();
            readers.Clear();
            store.DeleteAudio(finished);

            return finished;
        }
        finally
        {
            foreach (var reader in readers.Values) reader.Dispose();
        }
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
            yield return $"Only your microphone was recorded. Windows would not let Teezy hear the speakers: {problem}";
        }

        // Only a recording that stopped cleanly knows whether anything was heard; one that was
        // interrupted never got to say, and must not be accused of silence.
        if (info.Recorded <= TimeSpan.Zero) yield break;

        if (info.ThemProblem is null && !info.ThemHeard)
        {
            yield return "Nothing at all came from the speakers. If other people were talking, "
                         + "something on this computer is stopping Teezy from hearing them.";
        }

        if (!info.MeHeard)
        {
            yield return "Nothing at all came from the microphone. Check Windows' microphone privacy settings.";
        }
    }
}
