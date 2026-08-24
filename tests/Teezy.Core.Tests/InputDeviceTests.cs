using Shouldly;
using Teezy.Core;
using Teezy.Core.Abstractions;
using Teezy.Core.Dictionary;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Choosing which microphone to record from.</summary>
/// <remarks>
/// The failure this guards against is the quiet one. A device setting that does not reach the
/// capture, or that reaches it a restart later, produces no error and no crash — just a week
/// of dictating through the laptop's far-field array while Settings shows the headset.
/// </remarks>
public class InputDeviceTests
{
    private sealed record Harness(
        DictationController Controller, FakeHotkey Hotkey, FakeCapture Capture);

    private static Harness Build(Func<TeezySettings> settings)
    {
        var hotkey = new FakeHotkey();
        var capture = new FakeCapture();
        var path = Path.Combine(Path.GetTempPath(), $"teezy-{Guid.NewGuid():N}.txt");

        var controller = new DictationController(
            hotkey, capture, new FakeTranscriber(), new FakeInjector(),
            new DictionaryStore(path), settings);

        return new Harness(controller, hotkey, capture);
    }

    [Fact]
    public void TheChosenDeviceReachesTheCapture()
    {
        var h = Build(() => new TeezySettings { InputDeviceId = "headset", MinimumHoldMilliseconds = 0 });

        h.Hotkey.Press();

        h.Capture.PreferredDeviceId.ShouldBe("headset");
    }

    [Fact]
    public void NoChoiceLeavesTheDeviceToWindows()
    {
        var h = Build(() => new TeezySettings { MinimumHoldMilliseconds = 0 });

        h.Hotkey.Press();

        h.Capture.PreferredDeviceId.ShouldBeNull();
    }

    [Fact]
    public async Task ChangingTheDeviceAppliesOnTheNextHoldRatherThanTheNextRestart()
    {
        // Read per utterance for exactly this reason. Someone who has just picked a different
        // microphone in Settings will test it immediately, and "it takes effect when you
        // restart Teezy" reads as "the setting does nothing".
        var settings = new TeezySettings { InputDeviceId = "array", MinimumHoldMilliseconds = 0 };
        var h = Build(() => settings);

        h.Hotkey.Press();
        h.Capture.PreferredDeviceId.ShouldBe("array");
        h.Capture.Emit();
        h.Hotkey.Release();

        // The tail runs fire-and-forget, and a press arriving during it is rejected outright.
        // Waiting here is not incidental to the test: it is the state the user is in when
        // they change the setting and hold the key again.
        await WaitForIdle(h.Controller);

        settings = settings with { InputDeviceId = "headset" };

        h.Hotkey.Press();
        h.Capture.PreferredDeviceId.ShouldBe("headset");
    }

    private static async Task WaitForIdle(DictationController c, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (c.State != DictationState.Idle && Environment.TickCount64 < deadline)
            await Task.Delay(5);
        c.State.ShouldBe(DictationState.Idle);
    }

    [Fact]
    public void AnUnpluggedDeviceStillRecords()
    {
        // Falling back is the whole point: a chosen microphone that is not there must not
        // turn the hotkey into a dead key.
        var h = Build(() => new TeezySettings { InputDeviceId = "gone", MinimumHoldMilliseconds = 0 });
        h.Capture.MissingDeviceId = "gone";

        h.Hotkey.Press();

        h.Controller.State.ShouldBe(DictationState.Listening);
        h.Capture.IsRunning.ShouldBeTrue();
        h.Capture.UsingFallbackDevice.ShouldBeTrue();
    }

    [Fact]
    public void SettingsRememberTheDeviceAndItsName()
    {
        var path = Path.Combine(Path.GetTempPath(), $"teezy-settings-{Guid.NewGuid():N}.json");
        try
        {
            new TeezySettings { InputDeviceId = "abc", InputDeviceName = "Headset" }.Save(path);

            var loaded = TeezySettings.Load(path);
            loaded.InputDeviceId.ShouldBe("abc");

            // The name is carried purely so an absent device can be named in Settings rather
            // than shown as an endpoint id nobody can read.
            loaded.InputDeviceName.ShouldBe("Headset");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASettingsFileFromBeforeThePickerFollowsWindows() =>
        // Every existing install is this case, and its behaviour must not change.
        TeezySettings.Load(WriteTemp("""{ "NumThreads": 4 }""")).InputDeviceId.ShouldBeNull();

    private static string WriteTemp(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"teezy-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
