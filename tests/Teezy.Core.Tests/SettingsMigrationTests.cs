using Shouldly;
using Teezy.Core;
using Teezy.Core.Hotkeys;
using Xunit;

namespace Teezy.Core.Tests;

public class SettingsMigrationTests
{
    private static TeezySettings Load(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"teezy-settings-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try { return TeezySettings.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ConvertsTheOldSingleKeyFormat() =>
        // Silently resetting someone's hotkey to the default would be worse than any amount
        // of migration code.
        Load("""{ "PushToTalkKey": "RightControl" }""")
            .Hotkey.ShouldBe(new Hotkey(HotkeyKey.RightControl));

    [Fact]
    public void ConvertsRightShiftFaithfully() =>
        // Migration is faithful even though Shift is now discouraged: changing someone's
        // hotkey behind their back is not a migration, it is a surprise.
        Load("""{ "PushToTalkKey": "RightShift" }""")
            .Hotkey.ShouldBe(new Hotkey(HotkeyKey.RightShift));

    [Fact]
    public void DropsTheLegacyFieldSoTheMigrationSticks()
    {
        var migrated = Load("""{ "PushToTalkKey": "Pause" }""");
        migrated.LegacyPushToTalkKey.ShouldBeNull();
    }

    [Fact]
    public void ReadsTheNewCombinationFormat() =>
        Load("""{ "Hotkey": { "Keys": ["Control", "Windows"] } }""")
            .Hotkey.ShouldBe(Hotkey.Default);

    [Fact]
    public void AFileWithNoHotkeyAtAllStillGetsAUsableOne() =>
        // Hand-edited or truncated files must not leave the app with nothing to listen for.
        Load("""{ "NumThreads": 4 }""").Hotkey.ShouldBe(Hotkey.Default);

    [Fact]
    public void AnUnknownLegacyKeyFallsBackToTheDefault() =>
        Load("""{ "PushToTalkKey": "SomethingRemoved" }""").Hotkey.ShouldBe(Hotkey.Default);

    [Fact]
    public void OtherSettingsSurviveMigration()
    {
        var migrated = Load("""{ "PushToTalkKey": "RightControl", "NumThreads": 8, "ShowHud": false }""");
        migrated.NumThreads.ShouldBe(8);
        migrated.ShowHud.ShouldBeFalse();
    }

    [Fact]
    public void SavedSettingsRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"teezy-settings-{Guid.NewGuid():N}.json");
        var original = new TeezySettings { Hotkey = new Hotkey(HotkeyKey.Control, HotkeyKey.Alt) };
        original.Save(path);

        TeezySettings.Load(path).Hotkey.ShouldBe(original.Hotkey);
        File.ReadAllText(path).ShouldNotContain("PushToTalkKey");

        File.Delete(path);
    }
}
