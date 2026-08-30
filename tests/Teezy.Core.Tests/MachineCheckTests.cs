using Shouldly;
using Teezy.Core.Speech;
using Xunit;

namespace Teezy.Core.Tests;

public class MachineCheckTests
{
    private static MachineCheckResult Result(int best, int previous, params (int Threads, double Ms)[] runs) =>
        new([.. runs.Select(r => new ThreadResult(r.Threads, r.Ms))], best, previous);

    // ---- candidates ----

    [Fact]
    public void NeverOffersMoreThreadsThanTheMachineHas() =>
        ThreadCandidates.For(2).ShouldBe([1, 2]);

    [Fact]
    public void CapsTheSweepSoASlowMachineStillFinishesIt() =>
        // Four candidates, each costing a model reload plus three decodes. On a machine five
        // times slower than this one that is already most of a minute.
        ThreadCandidates.For(64).Count.ShouldBeLessThanOrEqualTo(4);

    [Fact]
    public void AlwaysTestsTheMachinesOwnCoreCount() =>
        // The first version trimmed the ladder from the top and never tried 8 threads on an
        // 8-core machine — the single most interesting value, and the one the README records
        // as measurably slower here. Verified against a real sweep, not just reasoned about.
        ThreadCandidates.For(8).ShouldContain(8);

    [Fact]
    public void AlwaysOffersTwoThreads() =>
        // The performance-core count on a hybrid CPU, and usually 2. A Core Ultra 5 135U
        // reports 14 processors but has two P-cores; the sweep that skipped 2 there was
        // measuring only the counts that were already too high.
        ThreadCandidates.For(16).ShouldContain(2);

    [Fact]
    public void DropsTheLeastInformativeRungWhenItHasToChoose() =>
        // 6 goes first: it sits between 4 and 8 and tells you least once both are measured.
        ThreadCandidates.For(16).ShouldBe([2, 4, 8, 16]);

    [Fact]
    public void CoversAHybridLaptopsRealLadder() =>
        // The exact machine that exposed this — 12 cores, 14 threads, two of them fast.
        // Before this, the sweep ran 4, 6, 8 and 14 and never tried the value most likely
        // to win.
        ThreadCandidates.For(14).ShouldBe([2, 4, 8, 14]);

    [Fact]
    public void StillOffersOneThreadWhenThereIsRoom() =>
        // Rarely the winner, but the only candidate that answers "is threading helping here
        // at all", so it is last to be dropped rather than first.
        ThreadCandidates.For(4).ShouldBe([1, 2, 4]);

    [Fact]
    public void OffersAnOddCoreCountToo() =>
        ThreadCandidates.For(3).ShouldBe([1, 2, 3]);

    [Fact]
    public void AlwaysOffersSomething() =>
        // A machine reporting zero processors is nonsense, but returning an empty sweep would
        // turn that nonsense into a crash in the benchmark loop.
        ThreadCandidates.For(0).ShouldBe([1]);

    // ---- verdict ----

    [Fact]
    public void ReportsTheGainAgainstWhatWasSetBefore() =>
        Result(best: 4, previous: 8, (4, 400), (8, 500)).GainPercent.ShouldBe(20, 0.01);

    [Fact]
    public void AMarginalWinIsNotWorthChangingTheSettingFor() =>
        // 3% on a best-of-two benchmark is indistinguishable from another process borrowing
        // the CPU. The honest answer is "these are the same".
        Result(best: 4, previous: 8, (4, 970), (8, 1000)).IsWorthApplying.ShouldBeFalse();

    [Fact]
    public void AClearWinIsWorthApplying() =>
        Result(best: 4, previous: 8, (4, 700), (8, 1000)).IsWorthApplying.ShouldBeTrue();

    [Fact]
    public void TheCurrentSettingWinningIsNotAChange() =>
        Result(best: 4, previous: 4, (4, 400), (8, 900)).IsWorthApplying.ShouldBeFalse();

    [Fact]
    public void ClaimsNoGainWhenThePreviousSettingWasNeverMeasured()
    {
        // Someone had threads set to 6 and the sweep only tried 1, 2, 4 and 8. Reporting a
        // gain against a baseline that was never timed would be inventing one.
        var result = Result(best: 4, previous: 6, (1, 900), (2, 600), (4, 400), (8, 450));

        result.PreviousMilliseconds.ShouldBe(0);
        result.GainPercent.ShouldBe(0);
        result.IsWorthApplying.ShouldBeFalse();
    }
}
