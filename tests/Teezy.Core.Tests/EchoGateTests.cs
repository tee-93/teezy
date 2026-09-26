using Shouldly;
using Teezy.Core.Meetings;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>
/// The gate is measured against made-up loudness curves rather than real audio: everything it
/// decides comes from the two curves, so a test that writes them by hand says exactly what case
/// it is about — a laptop on speakers, a headset, someone talking over the call.
/// </summary>
public class EchoGateTests
{
    private const float Talk = 0.08f;   // someone speaking
    private const float Room = 0.001f;  // an empty, quiet frame

    /// <summary>A minute of far-end speech in bursts, and nothing else.</summary>
    private static float[] FarEnd(int frames = 600)
    {
        var them = new float[frames];
        for (var i = 0; i < frames; i++)
        {
            // Six seconds of talk, two of pause, which is roughly how a call sounds.
            them[i] = i % 80 < 60 ? Talk : Room;
        }

        return them;
    }

    /// <summary>What the microphone hears of <paramref name="them"/>: quieter, and later.</summary>
    private static float[] EchoOf(float[] them, double coupling = 0.3, int lag = 2)
    {
        var me = new float[them.Length];
        for (var i = 0; i < me.Length; i++)
        {
            me[i] = i - lag >= 0 ? (float)(them[i - lag] * coupling) : Room;
            if (me[i] < Room) me[i] = Room;
        }

        return me;
    }

    [Fact]
    public void TheDelayAndLoudnessOfTheEchoAreMeasuredFromTheRecordings()
    {
        var them = FarEnd();
        var me = EchoOf(them, coupling: 0.3, lag: 2);

        var path = EchoGate.Measure(me, them);

        path.Found.ShouldBeTrue();
        path.LagFrames.ShouldBe(2);
        path.Delay.ShouldBe(TimeSpan.FromMilliseconds(200));
        path.Coupling.ShouldBe(0.3, tolerance: 0.05);
    }

    [Fact]
    public void AMicrophoneHearingOnlyTheSpeakersIsLeftOutEntirely()
    {
        var them = FarEnd();
        var me = EchoOf(them);

        var marked = EchoGate.EchoFrames(me, them, EchoGate.Measure(me, them));

        // Every frame carrying echo is marked; the silent ones do not matter either way.
        for (var i = 2; i < me.Length; i++)
        {
            if (them[i - 2] >= Talk) marked[i].ShouldBeTrue($"frame {i} is echo");
        }
    }

    [Fact]
    public void TalkingOverThemIsKept()
    {
        var them = FarEnd();
        var me = EchoOf(them);

        // Ten frames where you answer over the top: your own voice, not the room's return.
        for (var i = 100; i < 110; i++) me[i] = Talk;

        var marked = EchoGate.EchoFrames(me, them, EchoGate.Measure(me, them));

        for (var i = 100; i < 110; i++) marked[i].ShouldBeFalse($"frame {i} is you");
    }

    [Fact]
    public void YourOwnVoiceInThePausesIsKept()
    {
        var them = FarEnd();
        var me = EchoOf(them);
        for (var i = 60; i < 80; i++) me[i] = Talk;   // talking while they are not

        var marked = EchoGate.EchoFrames(me, them, EchoGate.Measure(me, them));

        for (var i = 60; i < 80; i++) marked[i].ShouldBeFalse($"frame {i} is you");
    }

    [Fact]
    public void AHeadsetMeasuresNoEchoPathAtAllAndNothingIsRemoved()
    {
        var them = FarEnd();

        // With a headset the microphone only ever carries you, and when you talk has nothing to
        // do with when they do: here, only while they are quiet.
        var me = new float[them.Length];
        for (var i = 0; i < me.Length; i++) me[i] = them[i] >= Talk ? Room : Talk;

        var path = EchoGate.Measure(me, them);

        path.Found.ShouldBeFalse();
        EchoGate.EchoFrames(me, them, path).ShouldAllBe(f => !f);
    }

    [Fact]
    public void ARecordingWithNothingFromTheSpeakersIsLeftAlone()
    {
        var me = FarEnd();
        var them = new float[me.Length];
        Array.Fill(them, Room);

        EchoGate.Measure(me, them).Found.ShouldBeFalse();
    }

    [Fact]
    public void AVeryShortRecordingIsNotEnoughToMeasureAnythingFrom()
    {
        var them = FarEnd(frames: 10);
        var me = EchoOf(them);

        EchoGate.Measure(me, them).Found.ShouldBeFalse();
    }

    [Fact]
    public void TheRoomRingingOnAfterTheyStopIsAlsoLeftOut()
    {
        var them = FarEnd();
        var me = EchoOf(them);

        // The tail: two frames past the end of their speech, quieter than the echo itself.
        for (var i = 0; i < me.Length; i++)
        {
            if (i >= 2 && them[i - 2] < Talk && i >= 4 && them[i - 4] >= Talk) me[i] = Talk * 0.05f;
        }

        var marked = EchoGate.EchoFrames(me, them, EchoGate.Measure(me, them));

        marked[62].ShouldBeTrue();
        marked[63].ShouldBeTrue();
    }

    [Fact]
    public void MutedTimeIsCounted()
    {
        EchoGate.Muted(new[] { true, false, true, true }).ShouldBe(TimeSpan.FromMilliseconds(300));
    }

    // ---- the cutter, given what the gate marked ----

    [Fact]
    public void ExcludedFramesAreNotSentToTheModel()
    {
        var levels = new float[200];
        Array.Fill(levels, Talk);
        var exclude = new bool[levels.Length];
        for (var i = 0; i < 100; i++) exclude[i] = true;

        var spans = SpeechCutter.Cut(levels, levels.Length * (long)SpeechCutter.FrameSamples, exclude);

        spans.ShouldNotBeEmpty();
        spans.ShouldAllBe(s => s.StartSample >= 97 * SpeechCutter.FrameSamples);
    }

    [Fact]
    public void ARecordingFullOfEchoStillHearsTheQuietThingsSaidOverIt()
    {
        // Fifteen seconds of loud echo, then one quiet remark of your own, then quiet. Measured
        // from every frame the room's floor would be the echo, and the remark would go with it.
        var levels = new float[300];
        var exclude = new bool[levels.Length];

        for (var i = 0; i < levels.Length; i++)
        {
            levels[i] = i < 150 ? Talk : Room;
            exclude[i] = i < 150;
        }

        for (var i = 150; i < 170; i++) levels[i] = Talk / 8;

        var spans = SpeechCutter.Cut(levels, levels.Length * (long)SpeechCutter.FrameSamples, exclude);

        spans.Count.ShouldBe(1);
        spans[0].Start.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(15));
    }
}
