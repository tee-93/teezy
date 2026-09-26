namespace Teezy.Core.Meetings;

/// <summary>How strict the echo gate is, in the units the numbers mean.</summary>
/// <param name="MarginDb">
/// How much louder than the estimated echo the microphone may be and still count as echo. Your
/// own voice into your own microphone is far louder than the same voice arriving from a speaker
/// across the room — measured on this laptop the room returns about a tenth of what it plays —
/// so a generous margin catches the ragged edges of the echo and still keeps you. It cannot run
/// away with itself: see <see cref="EchoGate"/>'s half-the-far-end cap.
/// </param>
/// <param name="Hangover">
/// Frames after the far end goes quiet that are still treated as echo: a room rings on a little,
/// and the tail is still not you.
/// </param>
/// <param name="MinCorrelation">
/// How alike the two recordings' loudness must be, at the best delay, before an echo path is
/// believed at all. A headset produces nothing like this, which is how the gate switches itself
/// off without a setting.
/// </param>
public sealed record EchoGateOptions(double MarginDb = 12, int Hangover = 2, double MinCorrelation = 0.25)
{
    public static readonly EchoGateOptions Default = new();
}

/// <summary>What the microphone picked up of the speakers: the delay, and how loudly.</summary>
/// <param name="LagFrames">Frames the echo arrives behind the far end, at 100 ms a frame.</param>
/// <param name="Coupling">Echo level over far-end level: 0.3 means the room returns a third of it.</param>
/// <param name="Correlation">How alike the two loudness curves are at that lag, 0 to 1.</param>
public readonly record struct EchoPath(int LagFrames, double Coupling, double Correlation)
{
    public static readonly EchoPath None = new(0, 0, 0);

    /// <summary>The delay, as time.</summary>
    public TimeSpan Delay => TimeSpan.FromMilliseconds(LagFrames * 100);

    /// <summary>Whether the speakers are reaching the microphone at all.</summary>
    public bool Found => Coupling > 0 && Correlation > 0;
}

/// <summary>
/// Finds the far end being overheard by the microphone, and marks those moments as not-you.
/// </summary>
/// <remarks>
/// <para>
/// Recording a call on a laptop's own speakers, every voice arrives twice: once cleanly on the
/// speaker recording, and once as room echo on the microphone. Dropping the duplicate after
/// transcription — comparing the words of both — only works when the recogniser hears the echo
/// well enough to produce similar words, and echo through a laptop microphone often is not.
/// </para>
/// <para>
/// So it is done in the sound instead, before anything is transcribed. The two recordings are
/// already the same length and on the same clock, so the far end's own loudness is the reference
/// signal a canceller would use. This is not cancellation — nothing is subtracted, and the
/// microphone file is left alone — it is a gate: the moments where the microphone is only
/// hearing the speakers are excluded from the pieces sent to the model.
/// </para>
/// <para>
/// Two numbers are measured from the recording itself rather than assumed: the <b>delay</b>,
/// because a speaker, a room and a microphone take time, and the <b>coupling</b>, because how
/// much comes back depends on the volume, the room and where the laptop is. With a headset
/// neither measures as anything, the gate does nothing, and both voices are kept — which is the
/// behaviour wanted, without asking anyone which they are wearing today.
/// </para>
/// </remarks>
public static class EchoGate
{
    /// <summary>800 ms. Further than any room, and beyond it the match is coincidence.</summary>
    private const int MaxLagFrames = 8;

    /// <summary>Below this the far end is not talking and there is nothing to echo.</summary>
    private const float Quiet = SpeechCutter.AbsoluteFloor;

    /// <summary>Fewer far-end frames than this is too little to measure anything from.</summary>
    private const int MinFarFrames = 20;

    /// <summary>
    /// However generous the margin, a microphone frame at least half as loud as the far end
    /// itself is somebody in this room talking. Room echo is an order of magnitude down; a
    /// gate that ever swallowed a reply would be worse than the duplicate it removes.
    /// </summary>
    private const double NearEndShare = 0.5;

    /// <summary>Measures the echo path from the two recordings' loudness curves.</summary>
    /// <param name="me">Microphone levels, one per 100 ms frame.</param>
    /// <param name="them">Speaker levels, one per 100 ms frame, on the same clock.</param>
    public static EchoPath Measure(
        IReadOnlyList<float> me, IReadOnlyList<float> them, EchoGateOptions? options = null)
    {
        options ??= EchoGateOptions.Default;
        if (me.Count == 0 || them.Count == 0) return EchoPath.None;
        if (them.Count(l => l >= Quiet) < MinFarFrames) return EchoPath.None;

        var bestLag = 0;
        var best = 0.0;

        for (var lag = 0; lag <= MaxLagFrames; lag++)
        {
            var r = Correlation(me, them, lag);
            if (r > best)
            {
                best = r;
                bestLag = lag;
            }
        }

        if (best < options.MinCorrelation) return EchoPath.None;

        var coupling = Coupling(me, them, bestLag);
        return coupling > 0 ? new EchoPath(bestLag, coupling, best) : EchoPath.None;
    }

    /// <summary>Which microphone frames are the speakers being overheard, and not you.</summary>
    /// <returns>One flag per frame of <paramref name="me"/>; true means "leave this out".</returns>
    public static bool[] EchoFrames(
        IReadOnlyList<float> me, IReadOnlyList<float> them, EchoPath path, EchoGateOptions? options = null)
    {
        options ??= EchoGateOptions.Default;
        var marked = new bool[me.Count];
        if (!path.Found) return marked;

        var margin = Math.Pow(10, options.MarginDb / 20);

        for (var i = 0; i < me.Count; i++)
        {
            var far = Far(them, i, path.LagFrames);
            if (far < Quiet) continue;

            // The echo this frame should hold, and the most the microphone may hold while
            // still being only that. Louder than this and someone is talking over them.
            if (me[i] <= Ceiling(far, path.Coupling, margin)) marked[i] = true;
        }

        // The room rings on after the far end stops; those frames are the same echo, quieter.
        for (var i = 0; i < marked.Length; i++)
        {
            if (!marked[i]) continue;

            var ceiling = Ceiling(Far(them, i, path.LagFrames), path.Coupling, margin);
            for (var k = 1; k <= options.Hangover && i + k < marked.Length; k++)
            {
                if (marked[i + k] || me[i + k] > ceiling) break;
                marked[i + k] = true;
            }
        }

        return marked;
    }

    /// <summary>How much of the frames is marked, as time.</summary>
    public static TimeSpan Muted(IReadOnlyList<bool> frames) =>
        TimeSpan.FromMilliseconds(frames.Count(f => f) * 100);

    /// <summary>The loudest the microphone can be this frame and still be only the room.</summary>
    private static double Ceiling(float far, double coupling, double margin) =>
        Math.Min(far * coupling * margin, far * NearEndShare);

    /// <summary>
    /// How loud the far end was when this frame of microphone was recorded — taken as the
    /// loudest of the frame before and after as well as the frame itself, because the delay is
    /// only known to the nearest frame and a word's first syllable would otherwise slip through
    /// against a reference that had not started yet.
    /// </summary>
    private static float Far(IReadOnlyList<float> them, int frame, int lag)
    {
        var loudest = 0f;
        for (var k = -1; k <= 1; k++)
        {
            var at = frame - lag + k;
            if (at >= 0 && at < them.Count && them[at] > loudest) loudest = them[at];
        }

        return loudest;
    }

    /// <summary>Pearson correlation of the two loudness curves with one delayed against the other.</summary>
    private static double Correlation(IReadOnlyList<float> me, IReadOnlyList<float> them, int lag)
    {
        var n = Math.Min(me.Count, them.Count + lag);
        if (n - lag < MinFarFrames) return 0;

        double sumMe = 0, sumThem = 0;
        for (var i = lag; i < n; i++)
        {
            sumMe += me[i];
            sumThem += them[i - lag];
        }

        var count = n - lag;
        var meanMe = sumMe / count;
        var meanThem = sumThem / count;

        double cross = 0, varMe = 0, varThem = 0;
        for (var i = lag; i < n; i++)
        {
            var a = me[i] - meanMe;
            var b = them[i - lag] - meanThem;
            cross += a * b;
            varMe += a * a;
            varThem += b * b;
        }

        if (varMe <= 0 || varThem <= 0) return 0;
        return cross / Math.Sqrt(varMe * varThem);
    }

    /// <summary>
    /// How much of the far end comes back, taken low rather than average: the quieter half of
    /// the moments the far end is talking are the ones where nobody here is talking over it, and
    /// those are the ones that show the room's own return.
    /// </summary>
    private static double Coupling(IReadOnlyList<float> me, IReadOnlyList<float> them, int lag)
    {
        var ratios = new List<double>();

        for (var i = 0; i < me.Count; i++)
        {
            var far = Far(them, i, lag);
            if (far < Quiet) continue;
            ratios.Add(me[i] / far);
        }

        if (ratios.Count < MinFarFrames) return 0;

        ratios.Sort();
        return ratios[(int)(ratios.Count * 0.4)];
    }
}
