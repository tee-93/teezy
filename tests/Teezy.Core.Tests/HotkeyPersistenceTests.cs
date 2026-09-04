using Shouldly;
using Teezy.Core;
using Teezy.Core.Hotkeys;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Saving a combination and getting the same one back.</summary>
public class HotkeyPersistenceTests
{
    private static TeezySettings RoundTrip(TeezySettings settings)
    {
        var path = Path.Combine(Path.GetTempPath(), $"teezy-hk-{Guid.NewGuid():N}.json");
        try
        {
            settings.Save(path);
            return TeezySettings.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Json(TeezySettings settings)
    {
        var path = Path.Combine(Path.GetTempPath(), $"teezy-hk-{Guid.NewGuid():N}.json");
        try
        {
            settings.Save(path);
            return File.ReadAllText(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheDefaultCombinationSurvivesASaveAndLoad() =>
        RoundTrip(new TeezySettings()).Hotkey.ShouldBe(Hotkey.Default);

    [Fact]
    public void BothKeysOfCtrlWinComeBack()
    {
        var loaded = RoundTrip(new TeezySettings { Hotkey = new Hotkey(HotkeyKey.Control, HotkeyKey.Windows) });

        loaded.Hotkey.Keys.ShouldBe([HotkeyKey.Control, HotkeyKey.Windows]);
    }

    [Fact]
    public void AThreeKeyCombinationSurvives()
    {
        var hotkey = new Hotkey(HotkeyKey.Control, HotkeyKey.Alt, HotkeyKey.Windows);

        RoundTrip(new TeezySettings { Hotkey = hotkey }).Hotkey.ShouldBe(hotkey);
    }

    [Fact]
    public void ARecordedSingleKeySurvives() =>
        RoundTrip(new TeezySettings { Hotkey = new Hotkey(HotkeyKey.F13) })
            .Hotkey.Keys.ShouldBe([HotkeyKey.F13]);

    [Fact]
    public void OnlyKeysIsWritten()
    {
        // Display, IsEmpty and Warnings are computed. Writing them puts three derived copies
        // of the hotkey in the file that nothing reads back, so a hand-edited file can
        // disagree with itself and the one the app believes is invisible.
        var json = Json(new TeezySettings());

        json.ShouldNotContain("\"Display\"");
        json.ShouldNotContain("\"IsEmpty\"");
        json.ShouldNotContain("\"Warnings\"");
    }
}
