using Shouldly;
using Teezy.Core;
using Teezy.Core.Calendar;
using Teezy.Core.Commands;
using Teezy.Core.Hotkeys;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Where a spoken diary question goes, and what it is allowed to reach.</summary>
public class AssistantCalendarTests
{
    /// <summary>A Monday morning, so "today" and "tomorrow" mean something fixed.</summary>
    private static readonly DateTimeOffset Now =
        new(2026, 9, 14, 9, 10, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 9, 14)));

    private sealed class FakeCalendar : ICalendar
    {
        public CalendarSource Source => CalendarSource.Microsoft;
        public bool IsConnected { get; set; } = true;
        public List<CalendarEvent> Events { get; } = [];
        public string? FailWith { get; set; }
        public bool WasRead { get; private set; }

        public Task<IReadOnlyList<CalendarEvent>> BetweenAsync(
            DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
        {
            WasRead = true;

            if (FailWith is { } why) throw new CalendarUnavailableException(why);

            return Task.FromResult<IReadOnlyList<CalendarEvent>>(Events);
        }
    }

    private sealed class FakeFallback : IAssistantFallback
    {
        public bool IsAvailable { get; set; } = true;
        public AssistantReply Reply { get; set; } = new(Answer: "I have no idea.");
        public string? Asked { get; private set; }

        public Task<AssistantReply> AskAsync(string spoken, CancellationToken ct = default)
        {
            Asked = spoken;
            return Task.FromResult(Reply);
        }
    }

    private sealed class FakeNarrator : IUntrustedNarrator
    {
        public bool IsAvailable { get; set; } = true;
        public string? Reply { get; set; } = "You’re free until two.";
        public string? Asked { get; private set; }
        public UntrustedMaterial? Shown { get; private set; }

        public Task<string?> AnswerAsync(
            string spoken,
            UntrustedMaterial material,
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            Asked = spoken;
            Shown = material;
            return Task.FromResult(Reply);
        }
    }

    private static CalendarEvent Meeting(string subject, int hour, int minute = 0)
    {
        var start = Now.Date.AddHours(hour).AddMinutes(minute);
        var offset = TimeZoneInfo.Local.GetUtcOffset(start);

        return new CalendarEvent(
            subject,
            new DateTimeOffset(start, offset),
            new DateTimeOffset(start.AddHours(1), offset),
            false,
            null,
            CalendarSource.Microsoft);
    }

    private static async Task<AssistantOutcome> Speak(
        string said,
        ICalendar? calendar = null,
        IUntrustedNarrator? narrator = null,
        IAssistantFallback? fallback = null)
    {
        var hotkey = new FakeHotkey();
        var capture = new FakeCapture();
        var transcriber = new FakeTranscriber { Result = said };
        var settings = new TeezySettings { MinimumHoldMilliseconds = 0 };

        var session = new VoiceSession(hotkey, capture, transcriber, () => settings);

        AssistantOutcome? outcome = null;

        var assistant = new AssistantController(
            session,
            runner: null,
            fallback: fallback,
            calendar: calendar is null ? null : new CombinedCalendar([calendar]),
            narrator: narrator,
            now: () => Now);

        assistant.Finished += o => outcome = o;
        session.Start();

        hotkey.Press(HotkeyAction.Assistant);
        capture.Emit();
        hotkey.Release(HotkeyAction.Assistant);

        var deadline = Environment.TickCount64 + 5000;
        while (outcome is null && Environment.TickCount64 < deadline) await Task.Delay(5);

        outcome.ShouldNotBeNull();
        return outcome!;
    }

    [Fact]
    public async Task AnEverydayDiaryQuestionIsAnsweredWithoutAskingAnyone()
    {
        var calendar = new FakeCalendar();
        calendar.Events.Add(Meeting("Review", 14));

        var fallback = new FakeFallback();
        var narrator = new FakeNarrator();

        var outcome = await Speak("what's on today", calendar, narrator, fallback);

        outcome.Result.ShouldBe(AssistantResult.Answered);
        outcome.Message.ShouldBe("One thing today: Review at 2pm.");

        // The entire argument for composing these locally: the common questions are instant,
        // free, and the diary never leaves the machine.
        fallback.Asked.ShouldBeNull();
        narrator.Asked.ShouldBeNull();
    }

    [Fact]
    public async Task WithNothingConnectedTheQuestionIsNotClaimed()
    {
        var calendar = new FakeCalendar { IsConnected = false };
        var fallback = new FakeFallback();

        var outcome = await Speak("what's on today", calendar, fallback: fallback);

        // Better that the general tier says it cannot see a calendar than that the diary path
        // answers "nothing" for an account that was never connected.
        calendar.WasRead.ShouldBeFalse();
        fallback.Asked.ShouldBe("what's on today");
        outcome.Message.ShouldBe("I have no idea.");
    }

    [Fact]
    public async Task SomethingThatIsNotAboutTheDiaryStillGoesToTheGeneralTier()
    {
        var calendar = new FakeCalendar();
        var fallback = new FakeFallback { Reply = new AssistantReply(Answer: "About 40 minutes.") };

        var outcome = await Speak("how long does rice take", calendar, fallback: fallback);

        calendar.WasRead.ShouldBeFalse();
        outcome.Message.ShouldBe("About 40 minutes.");
    }

    [Fact]
    public async Task AnAwkwardlyPhrasedQuestionGoesToTheNarratorWithTheDiary()
    {
        var calendar = new FakeCalendar();
        calendar.Events.Add(Meeting("Review", 14));

        var narrator = new FakeNarrator();
        var fallback = new FakeFallback();

        var outcome = await Speak("am i free this afternoon", calendar, narrator, fallback);

        outcome.Result.ShouldBe(AssistantResult.Answered);
        outcome.Message.ShouldBe("You’re free until two.");

        narrator.Shown!.Kind.ShouldBe(MaterialKind.Calendar);
        narrator.Shown!.Text.ShouldContain("Review");

        // And never to the tier that carries tools, which is the whole reason the narrator is
        // a separate interface.
        fallback.Asked.ShouldBeNull();
    }

    [Fact]
    public async Task WithoutANarratorTheEdgeOfWhatItCanDoIsAdmitted()
    {
        var calendar = new FakeCalendar();
        calendar.Events.Add(Meeting("Review", 14));

        var fallback = new FakeFallback();

        var outcome = await Speak("am i free this afternoon", calendar, fallback: fallback);

        outcome.Result.ShouldBe(AssistantResult.NotUnderstood);
        outcome.Message.ShouldBe("I can tell you what’s next, what’s on today, or tomorrow.");

        // Rather than handing the diary to a request that does carry tools.
        fallback.Asked.ShouldBeNull();
    }

    [Fact]
    public async Task ACalendarThatCannotBeReachedIsAFailureNotAnEmptyDay()
    {
        var calendar = new FakeCalendar { FailWith = "token expired" };

        var outcome = await Speak("what's on today", calendar);

        outcome.Result.ShouldBe(AssistantResult.Answered);
        outcome.Message.ShouldBe("I couldn’t reach your Microsoft calendar.");
    }

    [Fact]
    public async Task ThinkingIsAnnouncedBeforeTheDiaryIsRead()
    {
        var hotkey = new FakeHotkey();
        var capture = new FakeCapture();
        var transcriber = new FakeTranscriber { Result = "what's on today" };
        var settings = new TeezySettings { MinimumHoldMilliseconds = 0 };

        var session = new VoiceSession(hotkey, capture, transcriber, () => settings);

        var thought = false;
        AssistantOutcome? outcome = null;

        var assistant = new AssistantController(
            session,
            calendar: new CombinedCalendar([new FakeCalendar()]),
            now: () => Now);

        assistant.Thinking += () => thought = true;
        assistant.Finished += o => outcome = o;
        session.Start();

        hotkey.Press(HotkeyAction.Assistant);
        capture.Emit();
        hotkey.Release(HotkeyAction.Assistant);

        var deadline = Environment.TickCount64 + 5000;
        while (outcome is null && Environment.TickCount64 < deadline) await Task.Delay(5);

        // Reading a calendar is a network call, so the pill has to say something during it.
        thought.ShouldBeTrue();
    }
}
