using Shouldly;
using Teezy.Core.Meetings;
using Xunit;

namespace Teezy.Core.Tests;

public class SpeechCutterTests
{
    private const int Frame = SpeechCutter.FrameSamples;

    /// <summary>Frames of silence, with speech at the given frame ranges.</summary>
    private static float[] Levels(int frames, float room, params (int From, int To, float Level)[] speech)
    {
        var levels = Enumerable.Repeat(room, frames).ToArray();
        foreach (var (from, to, level) in speech)
        {
            for (var i = from; i < to; i++) levels[i] = level;
        }

        return levels;
    }

    private static IReadOnlyList<SpeechSpan> Cut(float[] levels) =>
        SpeechCutter.Cut(levels, (long)levels.Length * Frame);

    [Fact]
    public void Silence_is_not_sent_to_the_model()
    {
        Cut(Levels(600, 0f)).ShouldBeEmpty();
    }

    [Fact]
    public void One_utterance_becomes_one_padded_piece()
    {
        var spans = Cut(Levels(100, 0f, (20, 50, 0.1f)));

        spans.Count.ShouldBe(1);
        spans[0].StartSample.ShouldBe(17 * Frame);
        spans[0].EndSample.ShouldBe(53 * Frame);
    }

    [Fact]
    public void A_pause_after_a_long_enough_stretch_ends_the_piece()
    {
        var spans = Cut(Levels(120, 0f, (0, 50, 0.1f), (60, 100, 0.1f)));

        spans.Count.ShouldBe(2);
        spans[0].EndSample.ShouldBeLessThanOrEqualTo(spans[1].StartSample);
    }

    [Fact]
    public void A_breath_in_the_middle_of_a_sentence_does_not()
    {
        // Two seconds, a half-second pause, two seconds: too short to be worth splitting.
        var spans = Cut(Levels(100, 0f, (10, 30, 0.1f), (35, 55, 0.1f)));

        spans.Count.ShouldBe(1);
    }

    [Fact]
    public void A_long_monologue_is_cut_at_its_quietest_moment_without_overlap()
    {
        var levels = Levels(700, 0.1f);
        levels[150] = 0.06f;

        var spans = Cut(levels);

        spans[0].EndSample.ShouldBe(150 * Frame);
        spans[1].StartSample.ShouldBe(150 * Frame);

        foreach (var span in spans) span.Duration.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(28));
        for (var i = 1; i < spans.Count; i++) spans[i].StartSample.ShouldBe(spans[i - 1].EndSample);
        spans[^1].EndSample.ShouldBe(700 * Frame);
    }

    [Fact]
    public void A_noisy_room_raises_the_threshold_above_its_own_hum()
    {
        var spans = Cut(Levels(300, 0.01f, (100, 150, 0.1f)));

        spans.Count.ShouldBe(1);
        spans[0].StartSample.ShouldBe(97 * Frame);
    }

    [Fact]
    public void A_lone_click_is_ignored()
    {
        Cut(Levels(200, 0f, (80, 81, 0.3f))).ShouldBeEmpty();
    }

    [Fact]
    public void Levels_are_measured_per_tenth_of_a_second()
    {
        var samples = new float[Frame * 2 + 10];
        Array.Fill(samples, 0.5f, Frame, Frame);

        var levels = SpeechCutter.FrameLevels(samples);

        levels.Length.ShouldBe(3);
        levels[0].ShouldBe(0f);
        levels[1].ShouldBe(0.5f, 0.0001f);
    }
}
