using Shouldly;
using Teezy.Core;
using Teezy.Core.Calendar;
using Teezy.Core.Commands;
using Teezy.Core.Hotkeys;
using Teezy.Core.Tasks;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Spoken task questions and commands, through the whole assistant.</summary>
public sealed class AssistantTaskTests : IDisposable
{
    // Tuesday 22 September 2026, 9:00 am.
    private static readonly DateOnly Today = new(2026, 9, 22);
    private static readonly DateTimeOffset Now = TaskPlan.At(Today, new TimeOnly(9, 0));

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "teezy-voice-" + Guid.NewGuid().ToString("N"));
    private readonly TaskStore _tasks;

    public AssistantTaskTests() => _tasks = new TaskStore(Path.Combine(_folder, "tasks.json"), () => Now);

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private sealed class FakeFallback : IAssistantFallback
    {
        public AssistantReply Reply { get; set; } = new(Answer: "I have no idea.");
        public string? Asked { get; private set; }
        public bool IsAvailable => true;

        public Task<AssistantReply> AskAsync(string spoken, CancellationToken ct = default)
        {
            Asked = spoken;
            return Task.FromResult(Reply);
        }
    }

    private async Task<AssistantOutcome> Speak(string said, IAssistantFallback? fallback = null)
    {
        var hotkey = new FakeHotkey();
        var capture = new FakeCapture();
        var session = new VoiceSession(hotkey, capture, new FakeTranscriber { Result = said },
            () => new TeezySettings { MinimumHoldMilliseconds = 0 });

        AssistantOutcome? outcome = null;
        var assistant = new AssistantController(session, fallback: fallback, tasks: _tasks, now: () => Now);
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
    public async Task TodaysTasksAreAnsweredWithoutAskingAnyone()
    {
        _tasks.Add("Call Sam", due: Today, dueTime: new TimeOnly(14, 0));
        var fallback = new FakeFallback();

        var outcome = await Speak("What tasks do I have today?", fallback);

        outcome.Result.ShouldBe(AssistantResult.Answered);
        outcome.Message.ShouldBe("One task today: Call Sam at 2pm.");
        fallback.Asked.ShouldBeNull();
    }

    [Fact]
    public async Task WithNoCalendarWhatsOnTodayIsTheTaskList()
    {
        _tasks.Add("Expense report", due: Today);

        var outcome = await Speak("What's on today?");

        outcome.Message.ShouldBe("One task today: Expense report.");
    }

    [Fact]
    public async Task AddingATaskPutsItOnTheList()
    {
        var outcome = await Speak("Add a task to chase the Cessnock quote Friday at 2 p.m.");

        outcome.Result.ShouldBe(AssistantResult.Did);
        outcome.Message.ShouldBe("Added: Chase the Cessnock quote — due Friday 25 Sept at 2pm");

        var task = _tasks.Visible.Single();
        task.Due.ShouldBe(new DateOnly(2026, 9, 25));
        task.Remind.ShouldBe(TaskPlan.At(new DateOnly(2026, 9, 25), new TimeOnly(14, 0)));
    }

    [Fact]
    public async Task ClosingNeedsOneClearMatch()
    {
        var quote = _tasks.Add("Chase Cessnock Hospital quote");
        _tasks.Add("Call Sam");

        var outcome = await Speak("Mark the Cessnock quote as done");

        outcome.Result.ShouldBe(AssistantResult.Did);
        _tasks.Find(quote.Id)!.IsOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task CloseWithNoMatchingTaskFallsThrough()
    {
        _tasks.Add("Call Sam");
        var fallback = new FakeFallback();

        await Speak("Close Chrome", fallback);

        fallback.Asked.ShouldBe("Close Chrome");
    }

    [Fact]
    public async Task TheSmarterTierCanAddATaskInTheUsersWords()
    {
        var fallback = new FakeFallback { Reply = new(new VoiceCommand.AddTask("Ring the builder", "tomorrow 10am")) };

        var outcome = await Speak("Could you jot down that I should ring the builder tomorrow morning", fallback);

        outcome.Result.ShouldBe(AssistantResult.Did);
        var task = _tasks.Visible.Single();
        task.Title.ShouldBe("Ring the builder");
        task.Due.ShouldBe(Today.AddDays(1));
        task.DueTime.ShouldBe(new TimeOnly(10, 0));
    }
}
