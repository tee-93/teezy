namespace Teezy.Core.Speech;

/// <summary>One thread count and what it cost.</summary>
public sealed record ThreadResult(int Threads, double Milliseconds);

/// <summary>What the benchmark found.</summary>
/// <param name="Best">The thread count that decoded fastest.</param>
/// <param name="Previous">What was configured before the check ran.</param>
/// <remarks>
/// In Core rather than beside the recogniser so the arithmetic can be tested without the
/// native speech dependency — the same reason <see cref="HotwordEncoder"/> lives here.
/// </remarks>
public sealed record MachineCheckResult(
    IReadOnlyList<ThreadResult> Results,
    int Best,
    int Previous)
{
    public double BestMilliseconds =>
        Results.FirstOrDefault(r => r.Threads == Best)?.Milliseconds ?? 0;

    public double PreviousMilliseconds =>
        Results.FirstOrDefault(r => r.Threads == Previous)?.Milliseconds ?? 0;

    /// <summary>How much faster the winner is than what was set before, as a percentage.</summary>
    /// <remarks>
    /// Zero when the previous setting was not among those tested — which happens when someone
    /// has picked a thread count the sweep does not offer. Reporting a gain against a baseline
    /// that was never measured would be inventing one.
    /// </remarks>
    public double GainPercent => PreviousMilliseconds > 0
        ? (PreviousMilliseconds - BestMilliseconds) / PreviousMilliseconds * 100.0
        : 0;

    /// <summary>
    /// Worth changing the setting for, rather than noise.
    /// </summary>
    /// <remarks>
    /// Five percent on a best-of-two benchmark is about where a real difference stops being
    /// indistinguishable from another process borrowing the CPU. Below it the honest answer is
    /// "these are the same", not a settings change the user cannot feel.
    /// </remarks>
    public bool IsWorthApplying => Best != Previous && GainPercent >= 5;
}

/// <summary>Which thread counts are worth trying on a machine.</summary>
public static class ThreadCandidates
{
    /// <summary>
    /// Candidates per sweep. Each costs a model reload plus three decodes, so this is what
    /// keeps a slow machine finishing the check before the user gives up on it.
    /// </summary>
    private const int Cap = 4;

    /// <summary>
    /// Thread counts to try, most informative first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <b>core count</b> leads because it is the machine's own answer and the one value a
    /// sweep has no business omitting — an early version trimmed from the top and never tested
    /// 8 threads on an 8-core machine, which the README records as measurably slower here.
    /// </para>
    /// <para>
    /// <b>2 comes next, and that is not obvious.</b> This list previously dropped 1 and 2 first,
    /// reasoning that low counts are never the answer on a machine with more cores. That holds
    /// on a homogeneous CPU and is wrong on a hybrid one: an Intel Core Ultra 5 135U reports 14
    /// processors but has only <i>two</i> performance cores, the rest being E-cores and
    /// low-power E-cores. Asking for 4 there either pits hyperthread siblings against each
    /// other over the same vector units or spills the graph onto cores several times slower,
    /// and a parallel region finishes at the speed of its slowest thread. On any hybrid chip
    /// the performance-core count is a live candidate, and it is usually 2.
    /// </para>
    /// <para>
    /// 1 sits last rather than being dropped outright: it is rarely the winner, but it is the
    /// only candidate that answers "is threading helping at all here", and on a single-core
    /// machine it is the whole ladder.
    /// </para>
    /// </remarks>
    private static IEnumerable<int> PreferenceOrder(int cores)
    {
        yield return cores;
        yield return 2;
        yield return 4;
        yield return 8;
        yield return 6;
        yield return 1;
    }

    /// <summary>The thread counts to sweep on a machine with this many processors, ascending.</summary>
    public static IReadOnlyList<int> For(int processors)
    {
        var cores = Math.Max(1, processors);
        var chosen = new List<int>();

        foreach (var threads in PreferenceOrder(cores))
        {
            // Nothing above the processor count: the encoder is one graph, and oversubscribing
            // it is measurably slower rather than merely wasteful.
            if (threads > cores || chosen.Contains(threads)) continue;

            chosen.Add(threads);
            if (chosen.Count == Cap) break;
        }

        // Ascending, because the result is read as a table by a person.
        chosen.Sort();
        return chosen;
    }
}
