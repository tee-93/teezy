using Shouldly;
using Teezy.Core.Meetings;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>
/// Turning "someone at the far end" into "Speaker 2", and then into "Priya": the tidying of
/// what the model found, and the transcript file that carries the names.
/// </summary>
public class SpeakerLabelTests
{
    private static SpeakerSpan Span(double from, double to, int speaker) =>
        new(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to), speaker);

    [Fact]
    public void AFlickerOfTheOtherVoiceMidSentenceIsIgnored()
    {
        var smoothed = SpeakerSpans.Smooth(
            [Span(0, 8, 0), Span(8, 8.4, 1), Span(8.4, 15, 0)]);

        smoothed.Count.ShouldBe(1);
        smoothed[0].Speaker.ShouldBe(0);
        smoothed[0].End.ShouldBe(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void RealTurnsAreKeptApart()
    {
        var smoothed = SpeakerSpans.Smooth([Span(0, 8, 0), Span(8, 14, 1), Span(14, 20, 0)]);

        smoothed.Select(s => s.Speaker).ShouldBe([0, 1, 0]);
    }

    [Fact]
    public void ALineTakesTheVoiceThatHeldMostOfIt()
    {
        var spans = new[] { Span(0, 10, 0), Span(10, 30, 1) };

        SpeakerSpans.At(spans, TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(20)).ShouldBe(1);
        SpeakerSpans.At(spans, TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(11)).ShouldBe(0);
    }

    [Fact]
    public void ALineNobodyWasTalkingOverStaysUnnamed()
    {
        SpeakerSpans.At([Span(0, 10, 0)], TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(50)).ShouldBeNull();
    }

    [Fact]
    public void NamedVoicesAreWrittenIntoTheTranscriptAndReadBackOut()
    {
        var stats = new TranscriptionStats(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(1), 3, 0);
        var lines = new[]
        {
            new MeetingLine(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(3), Side.Me, "Morning all."),
            new MeetingLine(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(4), Side.Them, "Morning.", "Speaker 1"),
            new MeetingLine(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(4), Side.Them, "Shall we start?", "Speaker 2"),
        };

        var text = MeetingTranscript.Render(DateTimeOffset.Now, lines, stats, []);

        text.ShouldContain("[00:00:09] Speaker 1: Morning.");
        text.ShouldContain("[00:00:20] Speaker 2: Shall we start?");

        var read = MeetingTranscript.ParseLines(text);
        read[0].Side.ShouldBe(Side.Me);
        read[0].Speaker.ShouldBeNull();
        read[2].Side.ShouldBe(Side.Them);
        read[2].Speaker.ShouldBe("Speaker 2");

        MeetingTranscript.Speakers(text).ShouldBe(["Speaker 1", "Speaker 2"]);
    }

    [Fact]
    public void RenamingAVoiceChangesEveryLineOfItAndNothingElse()
    {
        const string transcript = """
            [00:00:09] Speaker 1: Morning.

            [00:00:20] Speaker 2: Shall we start? Speaker 1: was late again.

            [00:00:31] Speaker 1: Sorry, the train.
            """;

        var renamed = MeetingTranscript.Rename(transcript, "Speaker 1", "Priya");

        renamed.ShouldContain("[00:00:09] Priya: Morning.");
        renamed.ShouldContain("[00:00:31] Priya: Sorry, the train.");
        renamed.ShouldContain("Speaker 2: Shall we start? Speaker 1: was late again.");
        MeetingTranscript.Speakers(renamed).ShouldBe(["Priya", "Speaker 2"]);
    }

    [Fact]
    public void AnUnlabelledFarEndStillReadsAsThem()
    {
        var stats = new TranscriptionStats(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10), 1, 0);
        var text = MeetingTranscript.Render(
            DateTimeOffset.Now,
            [new MeetingLine(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(4), Side.Them, "Hello.")],
            stats,
            []);

        text.ShouldContain("] Them: Hello.");
        MeetingTranscript.Speakers(text).ShouldBeEmpty();
    }
}
