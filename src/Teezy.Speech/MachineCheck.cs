using System.Diagnostics;
using Teezy.Core.Abstractions;
using Teezy.Core.Speech;

namespace Teezy.Speech;

/// <summary>
/// Times the recogniser at several thread counts on this machine and picks the fastest.
/// </summary>
/// <remarks>
/// <para>
/// <b>This measures thread counts against each other, not absolute speed.</b> The audio it
/// decodes is synthesised, so the encoder — which dominates and costs the same whatever the
/// audio contains — is exercised honestly, while the decoder emits far fewer tokens than real
/// speech would and finishes early. The ratios between thread counts are meaningful; the
/// milliseconds are a floor.
/// </para>
/// <para>
/// The real-world figure comes from somewhere better: Insights reports the median transcribe
/// time and realtime factor from actual dictations. This exists to answer "which thread count
/// suits this CPU", which is the one thing about a slow machine that settings can fix.
/// </para>
/// </remarks>
public static class MachineCheck
{
    /// <summary>Long enough for per-call overhead to stop dominating, short enough that a
    /// slow machine finishes the whole sweep before the user gives up on it.</summary>
    private const int Seconds = 5;

    /// <summary>
    /// Sweeps the candidate thread counts and leaves the recogniser on the fastest.
    /// </summary>
    /// <remarks>
    /// Dictation is unavailable while this runs — the recogniser is torn down and rebuilt for
    /// each candidate — which is why the caller shows progress and why the sweep is capped at
    /// four candidates.
    /// </remarks>
    public static async Task<MachineCheckResult> RunAsync(
        ParakeetTranscriber transcriber,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var original = transcriber.Options;
        var candidates = ThreadCandidates.For(Environment.ProcessorCount);
        var samples = Synthesise(Seconds);
        var results = new List<ThreadResult>();

        try
        {
            foreach (var threads in candidates)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report($"Testing {threads} thread{(threads == 1 ? "" : "s")}…");

                await transcriber.ReloadAsync(original with { Threads = threads }, ct)
                    .ConfigureAwait(false);

                // Warm first. The opening decode pays for lazily-initialised buffers, and
                // billing that to the thread count would flatter whichever ran first.
                await transcriber.TranscribeAsync(samples, ct).ConfigureAwait(false);

                var best = double.MaxValue;
                for (var run = 0; run < 2; run++)
                {
                    var sw = Stopwatch.StartNew();
                    await transcriber.TranscribeAsync(samples, ct).ConfigureAwait(false);
                    best = Math.Min(best, sw.Elapsed.TotalMilliseconds);
                }

                // Best of two, not the mean: a background task stealing the CPU can only make
                // a run slower, so the fastest is the closest to what this setting can do.
                results.Add(new ThreadResult(threads, best));
            }
        }
        catch
        {
            // Put the recogniser back the way it was before letting the failure out —
            // otherwise a cancelled check leaves dictation on whatever it was mid-sweep.
            await Restore(transcriber, original).ConfigureAwait(false);
            throw;
        }

        var winner = results.MinBy(r => r.Milliseconds)!.Threads;

        progress?.Report("Applying…");
        await transcriber.ReloadAsync(original with { Threads = winner }, CancellationToken.None)
            .ConfigureAwait(false);

        return new MachineCheckResult(results, winner, original.Threads);
    }

    private static async Task Restore(ParakeetTranscriber transcriber, SpeechOptions original)
    {
        try
        {
            await transcriber.ReloadAsync(original, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TranscriberException)
        {
            // Nothing useful to do here; the caller is already handling a failure.
        }
    }

    /// <summary>
    /// Deterministic speech-shaped audio, so a re-run on the same machine is comparable.
    /// </summary>
    /// <remarks>
    /// Not real speech, and not pretending to be. A few harmonics in the vocal range with a
    /// syllable-rate envelope give the encoder a realistic amount of work — which is what
    /// scales with thread count — without shipping an audio file or recording the user.
    /// </remarks>
    internal static float[] Synthesise(int seconds)
    {
        var rate = AudioChunk.SampleRate;
        var samples = new float[seconds * rate];
        var random = new Random(20260824);

        for (var i = 0; i < samples.Length; i++)
        {
            var t = i / (double)rate;

            // A 4 Hz envelope is roughly syllable rate, so the signal starts and stops the
            // way speech does rather than droning.
            var envelope = 0.5 * (1 - Math.Cos(2 * Math.PI * 4 * t));

            var tone = Math.Sin(2 * Math.PI * 120 * t)
                       + 0.5 * Math.Sin(2 * Math.PI * 440 * t)
                       + 0.25 * Math.Sin(2 * Math.PI * 1300 * t);

            samples[i] = (float)(envelope * (tone * 0.2 + (random.NextDouble() - 0.5) * 0.05));
        }

        return samples;
    }
}
