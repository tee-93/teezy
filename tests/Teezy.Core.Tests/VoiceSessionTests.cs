using Shouldly;
using Teezy.Core;
using Teezy.Core.Hotkeys;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>The part every voice mode shares: hold, capture, release, transcribe.</summary>
public class VoiceSessionTests
{
    private sealed record Harness(
        VoiceSession Session, FakeHotkey Hotkey, FakeCapture Capture, FakeTranscriber Transcriber);

    private static Harness Build()
    {
        var hotkey = new FakeHotkey();
        var capture = new FakeCapture();
        var transcriber = new FakeTranscriber();
        var settings = new TeezySettings { MinimumHoldMilliseconds = 0 };

        return new Harness(
            new VoiceSession(hotkey, capture, transcriber, () => settings),
            hotkey, capture, transcriber);
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition() && Environment.TickCount64 < deadline) await Task.Delay(5);
        condition().ShouldBeTrue();
    }

    [Fact]
    public async Task HandsTheTranscriptToTheHandlerForThatBinding()
    {
        var h = Build();
        h.Transcriber.Result = "open chrome";

        VoiceResult? got = null;
        h.Session.Handle(HotkeyAction.Assistant, r => { got = r; return Task.CompletedTask; });
        h.Session.Start();

        h.Hotkey.Press(HotkeyAction.Assistant);
        h.Capture.Emit();
        h.Hotkey.Release(HotkeyAction.Assistant);

        await Until(() => got is not null);

        got!.Text.ShouldBe("open chrome");
        got.Action.ShouldBe(HotkeyAction.Assistant);
    }

    [Fact]
    public void AnActionWithNoHandlerNeverOpensTheMicrophone()
    {
        // An unhandled binding must be inert, not a hotkey that records and discards.
        var h = Build();
        h.Session.Handle(HotkeyAction.Dictate, _ => Task.CompletedTask);
        h.Session.Start();

        h.Hotkey.Press(HotkeyAction.Assistant);

        h.Capture.StartCount.ShouldBe(0);
        h.Session.State.ShouldBe(DictationState.Idle);
    }

    [Fact]
    public async Task OnlyTheHandlerForThePressedBindingRuns()
    {
        var h = Build();
        var dictated = false;
        var assisted = false;

        h.Session.Handle(HotkeyAction.Dictate, _ => { dictated = true; return Task.CompletedTask; });
        h.Session.Handle(HotkeyAction.Assistant, _ => { assisted = true; return Task.CompletedTask; });
        h.Session.Start();

        h.Hotkey.Press(HotkeyAction.Assistant);
        h.Capture.Emit();
        h.Hotkey.Release(HotkeyAction.Assistant);

        await Until(() => assisted);
        dictated.ShouldBeFalse();
    }

    [Fact]
    public async Task StaysBusyUntilTheHandlerFinishes()
    {
        // The reason the handler is awaited at all. Cleanup through the Claude tier can take
        // seconds, and a session that went idle when the recogniser finished would drop the
        // HUD while the user was still waiting for their text.
        var h = Build();
        var gate = new TaskCompletionSource();

        h.Session.Handle(HotkeyAction.Dictate, _ => gate.Task);
        h.Session.Start();

        h.Hotkey.Press();
        h.Capture.Emit();
        h.Hotkey.Release();

        await Until(() => h.Session.State == DictationState.Finishing);
        h.Session.State.ShouldBe(DictationState.Finishing);

        gate.SetResult();
        await Until(() => h.Session.State == DictationState.Idle);
    }

    [Fact]
    public void ReleasingADifferentBindingDoesNotEndThisUtterance()
    {
        // Real: adding a key hands over from one binding to another, so the release of the
        // binding we are not recording for arrives while we are still listening.
        var h = Build();
        h.Session.Handle(HotkeyAction.Dictate, _ => Task.CompletedTask);
        h.Session.Handle(HotkeyAction.Assistant, _ => Task.CompletedTask);
        h.Session.Start();

        h.Hotkey.Press(HotkeyAction.Dictate);
        h.Session.State.ShouldBe(DictationState.Listening);

        h.Hotkey.Release(HotkeyAction.Assistant);

        h.Session.State.ShouldBe(DictationState.Listening);
        h.Capture.IsRunning.ShouldBeTrue();
    }

    [Fact]
    public async Task ASilentUtteranceIsDroppedWithoutCallingTheHandler()
    {
        var h = Build();
        h.Transcriber.Result = "   ";

        var called = false;
        h.Session.Handle(HotkeyAction.Dictate, _ => { called = true; return Task.CompletedTask; });
        h.Session.Start();

        h.Hotkey.Press();
        h.Capture.Emit();
        h.Hotkey.Release();

        await Until(() => h.Session.State == DictationState.Idle);
        called.ShouldBeFalse();
    }

    [Fact]
    public void OnlyBindingsWithAHandlerAreArmed()
    {
        // Settings offers a combination per action; the session binds the ones it can serve.
        var h = Build();
        h.Session.Handle(HotkeyAction.Dictate, _ => Task.CompletedTask);
        h.Session.Start();

        h.Hotkey.Bindings.ShouldContainKey(HotkeyAction.Dictate);
        h.Hotkey.Bindings.ShouldNotContainKey(HotkeyAction.Assistant);
    }
}
