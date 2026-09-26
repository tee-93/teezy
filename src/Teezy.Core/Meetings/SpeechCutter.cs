using Teezy.Core.Abstractions;

namespace Teezy.Core.Meetings;

/// <summary>A stretch of a recording worth sending to the speech model.</summary>
public readonly record struct SpeechSpan(long StartSample, long EndSample)
{
    public long Length => EndSample - StartSample;

    public TimeSpan Start => TimeSpan.FromSeconds((double)StartSample / AudioChunk.SampleRate);

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Length / AudioChunk.SampleRate);
}

/// <summary>Splits a long recording into pieces the speech model can take, at pauses.</summary>
/// <remarks>
/// <para>
/// <b>Splitting is not optional.</b> Parakeet's encoder fails outright past about six minutes,
/// so a meeting has to go in pieces. The only question is where the seams fall, and a seam
/// through the middle of a word costs that word. So a piece ends at a real pause wherever one
/// exists, and only a long unbroken stretch of talk is cut by force — at its quietest moment.
/// </para>
/// <para>
/// <b>Loudness, not a voice-activity model.</b> sherpa-onnx ships a Silero VAD, but it needs a
/// model file this app does not yet download, and the job here is modest: find pauses, and
/// skip long silences so the model is not paid to transcribe them. The threshold follows the
/// recording's own noise floor, so an air-conditioned office and a quiet study both work.
/// If seams turn out to cost words in real meetings, Silero is the upgrade.
/// </para>
/// <para>
/// Works on levels per 100 ms frame rather than on samples, so an hour is 36,000 numbers and
/// the cutting logic is trivially testable.
/// </para>
/// </remarks>
public static class SpeechCutter
{
    public const int FrameSamples = AudioChunk.SampleRate / 10;

    /// <summary>Quieter than this is silence however quiet the room, about -48 dBFS.</summary>
    public const float AbsoluteFloor = 0.004f;

    /// <summary>The threshold never climbs past this, so constant loud talk still counts as talk.</summary>
    public const float Ceiling = 0.05f;

    private const int PadFrames = 3;          // 0.3 s either side, for soft onsets and tails
    private const int ShortGap = 8;           // a 0.8 s pause ends a piece that is long enough
    private const int LongGap = 20;           // a 2 s pause ends any piece
    private const int MinFrames = 40;         // 4 s: shorter pieces lose the context punctuation needs
    private const int MaxFrames = 280;        // 28 s: long enough for sentences, short enough to cut
    private const int MinSpeechFrames = 3;    // a lone click or cough is not worth a model call

    /// <summary>RMS level of each 100 ms frame of a recording.</summary>
    public static float[] FrameLevels(WavReader reader)
    {
        var frames = (int)((reader.SampleCount + FrameSamples - 1) / FrameSamples);
        var levels = new float[frames];

        // A minute at a time: enough to be fast, small enough not to load the hour.
        const int framesPerRead = 600;
        for (var f = 0; f < frames; f += framesPerRead)
        {
            var block = reader.Read((long)f * FrameSamples, framesPerRead * FrameSamples);
            Measure(block, levels.AsSpan(f, Math.Min(framesPerRead, frames - f)));
        }

        return levels;
    }

    /// <summary>RMS level of each 100 ms frame of <paramref name="samples"/>.</summary>
    public static float[] FrameLevels(ReadOnlySpan<float> samples)
    {
        var levels = new float[(samples.Length + FrameSamples - 1) / FrameSamples];
        Measure(samples, levels);
        return levels;
    }

    private static void Measure(ReadOnlySpan<float> samples, Span<float> levels)
    {
        for (var k = 0; k < levels.Length; k++)
        {
            var from = k * FrameSamples;
            var to = Math.Min(from + FrameSamples, samples.Length);
            if (from >= to) break;

            double sum = 0;
            for (var i = from; i < to; i++) sum += samples[i] * samples[i];
            levels[k] = (float)Math.Sqrt(sum / (to - from));
        }
    }

    /// <summary>The level above which a frame counts as someone talking.</summary>
    /// <param name="exclude">
    /// Frames to leave out of the reckoning, as <see cref="EchoGate"/> marks the ones where the
    /// microphone is only hearing the speakers. A recording full of echo has a high noise floor,
    /// and measuring the room from those frames would hide the quiet things actually said.
    /// </param>
    public static float Threshold(IReadOnlyList<float> levels, IReadOnlyList<bool>? exclude = null)
    {
        if (levels.Count == 0) return AbsoluteFloor;

        var sorted = exclude is null
            ? levels.ToArray()
            : levels.Where((_, i) => i >= exclude.Count || !exclude[i]).ToArray();

        if (sorted.Length == 0) return AbsoluteFloor;
        Array.Sort(sorted);

        // Even a busy meeting is quiet a sixth of the time, so this is the room, not a voice.
        var room = sorted[(int)(sorted.Length * 0.15)];
        return Math.Clamp(room * 2f, AbsoluteFloor, Ceiling);
    }

    /// <summary>The pieces to transcribe, in order, never overlapping.</summary>
    /// <param name="exclude">
    /// Frames to treat as silence however loud they are — the speakers overheard by the
    /// microphone. Excluding them here rather than dropping their words later means the model is
    /// never asked to transcribe the echo in the first place.
    /// </param>
    public static IReadOnlyList<SpeechSpan> Cut(
        IReadOnlyList<float> levels, long totalSamples, IReadOnlyList<bool>? exclude = null)
    {
        var threshold = Threshold(levels, exclude);
        var spans = new List<SpeechSpan>();
        var n = levels.Count;
        var previousEnd = 0;
        var i = 0;

        bool Speech(int frame) =>
            levels[frame] >= threshold && (exclude is null || frame >= exclude.Count || !exclude[frame]);

        while (i < n)
        {
            while (i < n && !Speech(i)) i++;
            if (i >= n) break;

            var start = i;
            var lastSpeech = i;
            var speechFrames = 1;
            var silence = 0;
            var forced = false;
            int end;

            for (var j = i + 1; ; j++)
            {
                if (j >= n)
                {
                    end = lastSpeech + 1;
                    break;
                }

                if (Speech(j))
                {
                    lastSpeech = j;
                    speechFrames++;
                    silence = 0;
                }
                else
                {
                    silence++;
                }

                var spoken = lastSpeech + 1 - start;
                if (silence >= LongGap || (silence >= ShortGap && spoken >= MinFrames))
                {
                    end = lastSpeech + 1;
                    break;
                }

                if (j + 1 - start >= MaxFrames)
                {
                    end = Quietest(levels, start + MinFrames, j);
                    forced = true;
                    break;
                }
            }

            // Padding softens the edges of natural pieces. A forced cut is not padded: the
            // next piece starts exactly there, and overlapping audio would transcribe twice.
            var from = Math.Max(previousEnd, start - PadFrames);
            var to = forced ? end : Math.Min(n, end + PadFrames);

            if (speechFrames >= MinSpeechFrames)
            {
                spans.Add(new SpeechSpan(
                    (long)from * FrameSamples,
                    Math.Min((long)to * FrameSamples, totalSamples)));
            }

            previousEnd = to;
            i = end;
        }

        return spans;
    }

    /// <summary>The quietest frame in a range; the latest one when several tie.</summary>
    private static int Quietest(IReadOnlyList<float> levels, int from, int to)
    {
        var best = from;
        for (var k = from; k <= to; k++)
        {
            if (levels[k] <= levels[best]) best = k;
        }

        return best;
    }
}
