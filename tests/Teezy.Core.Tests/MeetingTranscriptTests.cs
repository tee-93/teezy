using Shouldly;
using Teezy.Core.Meetings;
using Xunit;

namespace Teezy.Core.Tests;

public class MeetingTranscriptTests
{
    private static MeetingLine Line(double at, double seconds, Side side, string text) =>
        new(TimeSpan.FromSeconds(at), TimeSpan.FromSeconds(seconds), side, text);

    [Fact]
    public void The_microphone_overhearing_the_speakers_is_dropped()
    {
        var lines = new[]
        {
            Line(9.8, 4.5, Side.Them, "So the quarterly numbers look good overall."),
            Line(10, 4, Side.Me, "so the quarterly numbers look good"),
        };

        var kept = MeetingTranscript.DropEchoes(lines, out var dropped);

        dropped.ShouldBe(1);
        kept.ShouldHaveSingleItem().Side.ShouldBe(Side.Them);
    }

    [Fact]
    public void Talking_over_someone_is_kept()
    {
        var lines = new[]
        {
            Line(10, 5, Side.Them, "so the quarterly numbers look good overall"),
            Line(11, 3, Side.Me, "can we get the regional breakdown too"),
        };

        MeetingTranscript.DropEchoes(lines, out var dropped).Count.ShouldBe(2);
        dropped.ShouldBe(0);
    }

    [Fact]
    public void Repeating_their_words_later_is_kept()
    {
        var lines = new[]
        {
            Line(10, 4, Side.Them, "the deadline is next friday"),
            Line(30, 4, Side.Me, "the deadline is next friday"),
        };

        MeetingTranscript.DropEchoes(lines, out _).Count.ShouldBe(2);
    }

    [Fact]
    public void A_short_reply_over_them_is_never_taken_for_an_echo()
    {
        var lines = new[]
        {
            Line(10, 4, Side.Them, "does that work for you yes"),
            Line(12, 1, Side.Me, "yes"),
        };

        MeetingTranscript.DropEchoes(lines, out _).Count.ShouldBe(2);
    }

    [Fact]
    public void A_side_running_on_across_pieces_reads_as_one_paragraph()
    {
        var merged = MeetingTranscript.Merge(
        [
            Line(0, 10, Side.Me, "First part"),
            Line(10.5, 5, Side.Me, "second part."),
            Line(16, 3, Side.Them, "Reply."),
            Line(19.5, 2, Side.Me, "Answer."),
        ]);

        merged.Count.ShouldBe(3);
        merged[0].Text.ShouldBe("First part second part.");
        merged[0].Duration.ShouldBe(TimeSpan.FromSeconds(15.5));
        merged[1].Side.ShouldBe(Side.Them);
    }

    [Fact]
    public void Lines_from_both_sides_come_out_in_meeting_order()
    {
        var merged = MeetingTranscript.Merge(
        [
            Line(20, 2, Side.Them, "later"),
            Line(1, 2, Side.Me, "earlier"),
        ]);

        merged[0].Text.ShouldBe("earlier");
    }

    [Fact]
    public void The_transcript_says_when_how_long_and_who()
    {
        var stats = new TranscriptionStats(
            TimeSpan.FromMinutes(62), TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(20), 120, 3);

        var text = MeetingTranscript.Render(
            new DateTimeOffset(2026, 9, 13, 10, 0, 0, TimeSpan.FromHours(10)),
            [Line(4, 3, Side.Me, "Morning all."), Line(3725, 2, Side.Them, "Thanks everyone.")],
            stats,
            ["Microphone: Headset"]);

        text.ShouldContain("Recorded 1:02:00");
        text.ShouldContain("40:00 of audio transcribed");
        text.ShouldContain("(3.1x realtime)");
        text.ShouldContain("Note: Microphone: Headset");
        text.ShouldContain("[00:00:04] Me: Morning all.");
        text.ShouldContain("[01:02:05] Them: Thanks everyone.");
    }

    [Fact]
    public void An_empty_meeting_says_so()
    {
        var stats = new TranscriptionStats(TimeSpan.FromMinutes(1), TimeSpan.Zero, TimeSpan.FromSeconds(1), 0, 0);

        MeetingTranscript.Render(DateTimeOffset.Now, [], stats, []).ShouldContain("Nothing was heard.");
    }
}
