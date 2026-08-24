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
    /// <remarks>
    /// <para>
    /// More is not better past a point — the encoder is one graph and oversubscribing makes it
    /// slower, measurably so on the machine this was written on. Nothing above the processor
    /// count is offered, and the sweep is capped at four so a slow machine finishes it before
    /// the user gives up.
    /// </para>
    /// <para>
    /// When the cap bites, the <b>low</b> end is dropped rather than the high end. One and two
    /// threads are almost never the answer on a machine with more, and they are the slowest
    /// candidates to measure — whereas the processor count itself is the most interesting
    /// value on the ladder and is always offered. The first version of this trimmed from the
    /// top and never tested 8 threads on an 8-core machine.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<int> For(int processors)
    {
        var cores = Math.Max(1, processors);

        var ladder = new List<int> { 1, 2, 4, 6, 8 }.Where(n => n <= cores).ToList();
        if (!ladder.Contains(cores)) ladder.Add(cores);

        return ladder.Count <= 4 ? ladder : [.. ladder.TakeLast(4)];
    }
}
