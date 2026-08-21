using Shouldly;
using Wisper.Core.Abstractions;
using Xunit;

namespace Wisper.Core.Tests;

public class AudioLevelTests
{
    [Fact]
    public void DigitalSilenceReadsEmptyRatherThanThrowing() =>
        // log10(0) is negative infinity, and silence is exactly zero whenever microphone
        // access is blocked - the single most likely input in the field.
        AudioLevel.ToMeter(0).ShouldBe(0f);

    [Fact]
    public void FullScaleReadsFull() => AudioLevel.ToMeter(1.0).ShouldBe(1f);

    [Theory]
    // The whole point of the fix: ordinary speech must occupy the middle of the meter's
    // travel, not sit pinned near the floor the way linear scaling left it.
    [InlineData(0.02)]
    [InlineData(0.05)]
    [InlineData(0.08)]
    public void NormalSpeechLandsInTheUsableMiddle(double rms)
    {
        var level = AudioLevel.ToMeter(rms);
        level.ShouldBeGreaterThan(0.25f);
        level.ShouldBeLessThan(0.95f);
    }

    [Fact]
    public void LinearScalingWouldHaveFailedThisTest()
    {
        // Documents the original bug so it cannot quietly return: at 0.05 RMS the old
        // mapping (rms * 4) produced 0.2, which rendered as a row of motionless dots.
        const double SpeechRms = 0.05;
        (SpeechRms * 4).ShouldBeLessThan(0.25);
        AudioLevel.ToMeter(SpeechRms).ShouldBeGreaterThan(0.5f);
    }

    [Fact]
    public void IsMonotonic()
    {
        var previous = -1f;
        foreach (var rms in new[] { 0.0, 1e-6, 0.001, 0.005, 0.01, 0.05, 0.1, 0.3, 0.6, 1.0 })
        {
            var level = AudioLevel.ToMeter(rms);
            level.ShouldBeGreaterThanOrEqualTo(previous);
            previous = level;
        }
    }

    [Fact]
    public void StaysInRangeEvenWhenOverdriven() =>
        AudioLevel.ToMeter(12.0).ShouldBe(1f);
}
