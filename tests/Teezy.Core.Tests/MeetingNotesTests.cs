using Shouldly;
using Teezy.Assistant;
using Teezy.Core.Cost;
using Teezy.Core.Meetings;
using Teezy.Documents;
using Xunit;

namespace Teezy.Core.Tests;

public sealed class MeetingNotesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "teezy-notes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private const string Reply = """
        {
          "title": "Weekly operations update",
          "summary": ["The stock count is finished.", "The quoting tool goes live Monday."],
          "decisions": ["Training is due by Friday."],
          "follow_ups": [
            { "task": "Chase the supplier about door hardware", "owner": "Sarah", "due": "Wednesday" },
            { "task": "Finish the training module", "owner": "You", "due": "" }
          ],
          "open_questions": ["Who covers the Gold Coast site?"]
        }
        """;

    private static MeetingLine Line(int seconds, Side side, string text) =>
        new(TimeSpan.FromSeconds(seconds), TimeSpan.FromSeconds(3), side, text);

    [Fact]
    public void Notes_are_read_from_the_reply()
    {
        var notes = MeetingNotes.Parse(Reply);

        notes.Title.ShouldBe("Weekly operations update");
        notes.Summary.Count.ShouldBe(2);
        notes.Decisions.ShouldHaveSingleItem();
        notes.FollowUps[0].ShouldBe(new FollowUp("Chase the supplier about door hardware", "Sarah", "Wednesday"));
        notes.FollowUps[1].Due.ShouldBe("");
        notes.OpenQuestions.ShouldHaveSingleItem();
    }

    [Fact]
    public void A_reply_wrapped_in_a_fence_still_reads()
    {
        MeetingNotes.Parse($"Here are the notes:\n```json\n{Reply}\n```").FollowUps.Count.ShouldBe(2);
    }

    [Fact]
    public void Missing_and_blank_parts_become_empty_and_tasks_always_have_an_owner()
    {
        var notes = MeetingNotes.Parse("""
            { "title": " ", "summary": "First paragraph.\n\nSecond paragraph.",
              "follow_ups": [ { "task": "Send the deck", "owner": "", "due": "" }, { "task": "  " } ] }
            """);

        notes.Title.ShouldBe("Meeting");
        notes.Summary.Count.ShouldBe(2);
        notes.Decisions.ShouldBeEmpty();
        notes.FollowUps.ShouldHaveSingleItem().Owner.ShouldBe("Unassigned");
    }

    [Fact]
    public void A_reply_with_no_notes_in_it_is_refused()
    {
        Should.Throw<MeetingSummaryException>(() => MeetingNotes.Parse("Sorry, I can't help with that."));
        Should.Throw<MeetingSummaryException>(() => MeetingNotes.Parse("{ not json }"));
    }

    [Fact]
    public void Transcript_lines_come_back_out_of_the_transcript_file()
    {
        var stats = new TranscriptionStats(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1), 2, 0);
        var text = MeetingTranscript.Render(
            DateTimeOffset.Now,
            [Line(4, Side.Me, "Morning all."), Line(3725, Side.Them, "Thanks: see you Friday.")],
            stats,
            ["Microphone: Headset"]);

        var lines = MeetingTranscript.ParseLines(text.ReplaceLineEndings("\r\n"));

        lines.Count.ShouldBe(2);
        lines[0].Side.ShouldBe(Side.Me);
        lines[0].Text.ShouldBe("Morning all.");
        lines[1].At.ShouldBe(TimeSpan.FromSeconds(3725));
        lines[1].Text.ShouldBe("Thanks: see you Friday.");
    }

    [Fact]
    public void Notes_are_kept_beside_the_meeting()
    {
        var store = new MeetingStore(_root);
        var meeting = store.Create(DateTimeOffset.Now);
        var saved = new SavedNotes(MeetingNotes.Parse(Reply), "claude-opus-5", new TokenUsage(12_000, 900, 0, 0), DateTimeOffset.Now);

        store.SaveNotes(meeting, saved);

        meeting.HasNotes.ShouldBeTrue();
        var loaded = store.LoadNotes(meeting).ShouldNotBeNull();
        loaded.Model.ShouldBe("claude-opus-5");
        loaded.Tokens.ShouldBe(saved.Tokens);
        loaded.Notes.FollowUps.ShouldBe(saved.Notes.FollowUps);
    }

    [Fact]
    public void The_pdf_renders_with_the_transcript_on_a_page_of_its_own()
    {
        // Rendering needs real fonts; a machine without Windows' fonts has nothing to test here.
        var fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!File.Exists(Path.Combine(fonts, "segoeui.ttf"))) return;

        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "notes.pdf");
        var info = new MeetingInfo(DateTimeOffset.Now, TimeSpan.FromMinutes(42));
        var saved = new SavedNotes(MeetingNotes.Parse(Reply), "claude-opus-5", null, DateTimeOffset.Now);

        var pages = MeetingNotesPdf.Write(path, info, saved, [Line(4, Side.Me, "Morning all."), Line(9, Side.Them, "Morning.")]);

        pages.ShouldBe(2);
        File.ReadAllBytes(path)[..4].ShouldBe("%PDF"u8.ToArray());
        File.Exists(path + ".tmp").ShouldBeFalse();
    }

    [Fact]
    public void The_request_says_when_and_frames_the_transcript_as_material()
    {
        var info = new MeetingInfo(new DateTimeOffset(2026, 9, 13, 9, 30, 0, TimeSpan.FromHours(10)), TimeSpan.FromMinutes(31));

        var request = ClaudeMeetingSummariser.Compose(info, [Line(4, Side.Me, "Morning all."), Line(9, Side.Them, "Morning.")]);

        request.ShouldContain("lasting 31:00");
        request.ShouldContain("<transcript>");
        request.ShouldContain("[00:00:04] Me: Morning all.");
        request.ShouldContain("[00:00:09] Them: Morning.");
        request.ShouldNotContain("Only the user's microphone");
    }

    [Fact]
    public async Task Without_a_key_it_says_so_instead_of_calling()
    {
        var summariser = new ClaudeMeetingSummariser(() => null);

        summariser.IsAvailable.ShouldBeFalse();
        await Should.ThrowAsync<MeetingSummaryException>(() =>
            summariser.SummariseAsync(new MeetingInfo(DateTimeOffset.Now), [Line(1, Side.Me, "Hi.")]));
    }
}
