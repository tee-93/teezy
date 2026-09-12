using Shouldly;
using Teezy.Core;
using Teezy.Core.Commands;
using Teezy.Core.Hotkeys;
using Xunit;

namespace Teezy.Core.Tests;

public class AssistantControllerTests
{
    private sealed class FakeRunner : ICommandRunner
    {
        public VoiceCommand? Ran { get; private set; }
        public string Reply { get; set; } = "Done";
        public string? FailWith { get; set; }

        public Task<string> RunAsync(VoiceCommand command, CancellationToken ct = default)
        {
            if (FailWith is { } why) throw new CommandFailedException(why);
            Ran = command;
            return Task.FromResult(Reply);
        }
    }

    private static (VoiceSession Session, FakeHotkey Hotkey, FakeCapture Capture, FakeTranscriber Transcriber)
        Build()
    {
        var hotkey = new FakeHotkey();
        var capture = new FakeCapture();
        var transcriber = new FakeTranscriber();
        var settings = new TeezySettings { MinimumHoldMilliseconds = 0 };

        return (new VoiceSession(hotkey, capture, transcriber, () => settings), hotkey, capture, transcriber);
    }

    private static async Task<AssistantOutcome> Speak(string said, ICommandRunner? runner = null)
    {
        var (session, hotkey, capture, transcriber) = Build();
        transcriber.Result = said;

        AssistantOutcome? outcome = null;
        var assistant = new AssistantController(session, runner);
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
    public async Task RunsTheCommandItUnderstood()
    {
        var runner = new FakeRunner { Reply = "Volume 40%" };

        var outcome = await Speak("set the volume to 40", runner);

        runner.Ran.ShouldBe(new VoiceCommand.SetVolume(40));
        outcome.Result.ShouldBe(AssistantResult.Did);
        outcome.Message.ShouldBe("Volume 40%");
    }

    [Fact]
    public async Task WhatItCannotDoComesBackWithWhatItHeard()
    {
        // The transcript is the whole point of the failure case: without it there is no way to
        // tell a misrecognition from an unsupported command, and they need different responses
        // from the user — say it again, versus stop asking for that.
        var outcome = await Speak("book me a table for two", new FakeRunner());

        outcome.Result.ShouldBe(AssistantResult.NotUnderstood);
        outcome.Command.ShouldBeNull();
        outcome.Heard.ShouldBe("book me a table for two");
    }

    [Fact]
    public async Task ACommandThatFailsIsReportedAsItselfNotAsMisunderstood()
    {
        // "I couldn't find Chrome" tells the user something completely different from
        // "I can't do that yet", so the two must not collapse into one message.
        var runner = new FakeRunner { FailWith = "Couldn’t find Chrome" };

        var outcome = await Speak("open chrome", runner);

        outcome.Result.ShouldBe(AssistantResult.Failed);
        outcome.Command.ShouldBe(new VoiceCommand.LaunchApp("chrome"));
        outcome.Message.ShouldBe("Couldn’t find Chrome");
    }

    [Fact]
    public async Task WithNoRunnerItUnderstandsAndTouchesNothing()
    {
        // The state this ships in before the actions are wired: the whole loop is visible and
        // nothing happens to the machine.
        var outcome = await Speak("lock the computer");

        outcome.Result.ShouldBe(AssistantResult.Did);
        outcome.Command.ShouldBe(new VoiceCommand.LockScreen());
        outcome.Message.ShouldBe("Would lock the PC");
    }

    // ---- the smarter tier ----

    private sealed class FakeFallback : IAssistantFallback
    {
        public bool IsAvailable { get; set; } = true;
        public AssistantReply Reply { get; set; } = AssistantReply.Nothing;
        public string? Asked { get; private set; }
        public string? Throw { get; set; }

        public Task<AssistantReply> AskAsync(string spoken, CancellationToken ct = default)
        {
            if (Throw is { } why) throw new AssistantUnavailableException(why);
            Asked = spoken;
            return Task.FromResult(Reply);
        }
    }

    private static async Task<AssistantOutcome> Speak(
        string said, ICommandRunner? runner, IAssistantFallback? fallback)
    {
        var (session, hotkey, capture, transcriber) = Build();
        transcriber.Result = said;

        AssistantOutcome? outcome = null;
        var assistant = new AssistantController(session, runner, fallback);
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
    public async Task WhatThePatternsAlreadyHandleNeverReachesTheModel()
    {
        // The whole economic and privacy argument for the local layer. If "volume up" went to
        // Claude, every command would cost money and leave the machine.
        var fallback = new FakeFallback();

        await Speak("volume up", new FakeRunner(), fallback);

        fallback.Asked.ShouldBeNull();
    }

    [Fact]
    public async Task WhatThePatternsDeclineIsHandedOver()
    {
        var fallback = new FakeFallback
        {
            Reply = new AssistantReply(Command: new VoiceCommand.SetVolume(25)),
        };
        var runner = new FakeRunner();

        var outcome = await Speak("make it quite a bit quieter please", runner, fallback);

        fallback.Asked.ShouldBe("make it quite a bit quieter please");
        runner.Ran.ShouldBe(new VoiceCommand.SetVolume(25));
        outcome.Result.ShouldBe(AssistantResult.Did);
    }

    [Fact]
    public async Task AnAnswerIsShownRatherThanRun()
    {
        var fallback = new FakeFallback { Reply = new AssistantReply(Answer: "It is half past four.") };

        var outcome = await Speak("what time is it", new FakeRunner(), fallback);

        outcome.Result.ShouldBe(AssistantResult.Answered);
        outcome.Message.ShouldBe("It is half past four.");
        outcome.Command.ShouldBeNull();
    }

    [Fact]
    public async Task ATierThatIsOffChangesNothing()
    {
        // Someone who has not switched it on must get exactly the behaviour they had before,
        // including the honest refusal.
        var fallback = new FakeFallback { IsAvailable = false };

        var outcome = await Speak("book me a table for two", new FakeRunner(), fallback);

        fallback.Asked.ShouldBeNull();
        outcome.Result.ShouldBe(AssistantResult.NotUnderstood);
    }

    [Fact]
    public async Task BeingUnreachableIsNotTheSameAsNotUnderstanding()
    {
        // Someone whose wifi is down should not go away rephrasing themselves.
        var fallback = new FakeFallback { Throw = "Couldn’t reach Claude." };

        var outcome = await Speak("what is the capital of Peru", new FakeRunner(), fallback);

        outcome.Result.ShouldBe(AssistantResult.Failed);
        outcome.Message.ShouldBe("Couldn’t reach Claude.");
    }

    [Fact]
    public async Task AModelThatOffersNothingUsefulIsARefusalNotAnEmptyAnswer()
    {
        var fallback = new FakeFallback { Reply = AssistantReply.Nothing };

        var outcome = await Speak("mumble mumble", new FakeRunner(), fallback);

        outcome.Result.ShouldBe(AssistantResult.NotUnderstood);
    }

    [Fact]
    public async Task DictationIsNotTheAssistantsBusiness()
    {
        var (session, hotkey, capture, transcriber) = Build();
        transcriber.Result = "open chrome";

        var fired = false;
        var assistant = new AssistantController(session);
        assistant.Finished += _ => fired = true;

        // Dictate has no handler here, so the press is inert and nothing should reach the
        // assistant even though the words would have matched a command.
        session.Start();
        hotkey.Press(HotkeyAction.Dictate);
        capture.Emit();
        hotkey.Release(HotkeyAction.Dictate);

        await Task.Delay(150);
        fired.ShouldBeFalse();
    }
}
