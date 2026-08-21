using Shouldly;
using Teezy.Core;
using Teezy.Core.Abstractions;
using Teezy.Core.Dictionary;
using Xunit;

namespace Teezy.Core.Tests;

public class DictationControllerTests
{
    private sealed record Harness(
        DictationController Controller,
        FakeHotkey Hotkey,
        FakeCapture Capture,
        FakeTranscriber Transcriber,
        FakeInjector Injector);

    private static Harness Build(TeezySettings? settings = null, string? dictionaryFile = null)
    {
        var hotkey = new FakeHotkey();
        var capture = new FakeCapture();
        var transcriber = new FakeTranscriber();
        var injector = new FakeInjector();

        var path = dictionaryFile ?? Path.Combine(Path.GetTempPath(), $"teezy-{Guid.NewGuid():N}.txt");
        var store = new DictionaryStore(path);

        var effective = settings ?? new TeezySettings { MinimumHoldMilliseconds = 0 };
        var controller = new DictationController(
            hotkey, capture, transcriber, injector, store, () => effective);

        return new Harness(controller, hotkey, capture, transcriber, injector);
    }

    /// <summary>Waits for the fire-and-forget tail to finish rather than sleeping a fixed
    /// amount, which would be flaky on a loaded machine.</summary>
    private static async Task WaitForIdle(DictationController c, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (c.State != DictationState.Idle && Environment.TickCount64 < deadline)
            await Task.Delay(5);
        c.State.ShouldBe(DictationState.Idle);
    }

    [Fact]
    public async Task HoldSpeakReleaseInjectsCleanedText()
    {
        var h = Build();
        h.Transcriber.Result = "um hello world";

        h.Hotkey.Press();
        h.Controller.State.ShouldBe(DictationState.Listening);
        h.Capture.Emit();
        h.Hotkey.Release();

        await WaitForIdle(h.Controller);
        h.Injector.Inserted.ShouldHaveSingleItem().ShouldBe("Hello world.");
    }

    [Fact]
    public async Task SecondPressDuringTranscriptionDoesNotDoubleInject()
    {
        // The bug this guards: transcription plus cleanup takes a few hundred milliseconds,
        // and a press arriving in that window would re-enter the tail, read the same buffer
        // and type the utterance twice.
        var h = Build();
        h.Transcriber.Gate = new TaskCompletionSource();

        h.Hotkey.Press();
        h.Capture.Emit();
        h.Hotkey.Release();

        // Now in Finishing, blocked inside TranscribeAsync.
        h.Controller.State.ShouldBe(DictationState.Finishing);

        h.Hotkey.Press();     // must be ignored
        h.Hotkey.Release();   // must be ignored

        h.Transcriber.Gate.SetResult();
        await WaitForIdle(h.Controller);

        h.Transcriber.Calls.ShouldBe(1);
        h.Injector.Inserted.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ShortTapIsIgnored()
    {
        var h = Build(new TeezySettings { MinimumHoldMilliseconds = 10_000 });

        h.Hotkey.Press();
        h.Capture.Emit();
        h.Hotkey.Release();

        await WaitForIdle(h.Controller);
        h.Transcriber.Calls.ShouldBe(0);
        h.Injector.Inserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task SilenceInjectsNothing()
    {
        var h = Build();
        h.Transcriber.Result = "   ";

        h.Hotkey.Press();
        h.Capture.Emit();
        h.Hotkey.Release();

        await WaitForIdle(h.Controller);
        h.Injector.Inserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReleaseWithNoAudioInjectsNothing()
    {
        var h = Build();
        h.Hotkey.Press();
        h.Hotkey.Release();     // no chunks emitted at all

        await WaitForIdle(h.Controller);
        h.Transcriber.Calls.ShouldBe(0);
    }

    [Fact]
    public void MicrophoneFailureSurfacesAsErrorNotCrash()
    {
        var h = Build();
        h.Capture.FailOnStart = true;

        string? reported = null;
        h.Controller.Failed += m => reported = m;

        h.Hotkey.Press();

        h.Controller.State.ShouldBe(DictationState.Error);
        reported.ShouldNotBeNull();
    }

    [Fact]
    public async Task AudioArrivingAfterReleaseIsNotAppended()
    {
        var h = Build();
        h.Transcriber.Gate = new TaskCompletionSource();

        h.Hotkey.Press();
        h.Capture.Emit(1600);
        h.Hotkey.Release();

        h.Capture.Emit(9999);   // late buffer from a device that hasn't fully stopped
        h.Transcriber.Gate.SetResult();
        await WaitForIdle(h.Controller);

        h.Transcriber.LastSampleCount.ShouldBe(1600);
    }

    [Fact]
    public async Task DictionaryAppliesEvenWithCleanupOff()
    {
        // Biasing only improves the odds; the correction pass is what guarantees a spelling,
        // so it must not be switchable off along with cleanup.
        var path = Path.Combine(Path.GetTempPath(), $"teezy-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(path, ["cloud code -> Claude Code"]);

        var h = Build(new TeezySettings { CleanupEnabled = false, MinimumHoldMilliseconds = 0 }, path);
        h.Transcriber.Result = "open cloud code";

        h.Hotkey.Press();
        h.Capture.Emit();
        h.Hotkey.Release();

        await WaitForIdle(h.Controller);
        h.Injector.Inserted.ShouldHaveSingleItem().ShouldBe("open Claude Code");

        File.Delete(path);
    }
}
