using Shouldly;
using Teezy.Core.Calendar;
using Teezy.Core.Sync;
using Xunit;

namespace Teezy.Core.Tests;

public class SyncTests
{
    // ---- the cipher ----

    [Fact]
    public void WhatIsSealedOpensWithTheSamePassphrase() =>
        SyncCipher.Open(SyncCipher.Seal("sk-ant-secret", "correct horse"), "correct horse")
            .ShouldBe("sk-ant-secret");

    [Fact]
    public void TheSealedFileDoesNotContainTheSecret() =>
        SyncCipher.Seal("sk-ant-secret", "correct horse").ShouldNotContain("sk-ant");

    [Fact]
    public void TheWrongPassphraseSaysSo() =>
        Should.Throw<SyncUnlockException>(() =>
                SyncCipher.Open(SyncCipher.Seal("x", "correct horse"), "wrong horse"))
            .Message.ShouldContain("passphrase");

    [Fact]
    public void ATamperedFileIsRefused()
    {
        // Through the JSON, not by find-and-replace on the text: the serializer escapes '+' in
        // base64, so a textual replace silently changed nothing whenever the data held one.
        var file = System.Text.Json.Nodes.JsonNode.Parse(SyncCipher.Seal("the real settings", "pw"))!;
        var bytes = Convert.FromBase64String(file["data"]!.GetValue<string>());
        bytes[0] ^= 1;
        file["data"] = Convert.ToBase64String(bytes);
        var tampered = file.ToJsonString();

        Should.Throw<SyncUnlockException>(() => SyncCipher.Open(tampered, "pw"));
    }

    [Fact]
    public void SomethingThatIsNotASyncFileSaysSo() =>
        Should.Throw<SyncUnlockException>(() => SyncCipher.Open("{\"hello\":1}", "pw"))
            .Message.ShouldContain("isn’t a TeezyFlow sync file");

    [Fact]
    public void EachSealUsesAFreshSaltAndNonce() =>
        SyncCipher.Seal("same", "pw").ShouldNotBe(SyncCipher.Seal("same", "pw"));

    // ---- what travels ----

    private static readonly ConnectedAccount SignedIn = new("ms1", "me@work", CalendarSource.Microsoft, CalendarProfile.Work);
    private static readonly ConnectedAccount Link = new("ics1", "Work diary", CalendarSource.Ics, CalendarProfile.Work);

    [Fact]
    public void ThisComputersOwnSettingsStayBehind()
    {
        var portable = new TeezySettings
        {
            InputDeviceId = "mic-123",
            NumThreads = 2,
            ModelPath = @"D:\models",
            SyncFolder = @"C:\OneDrive\TeezyFlow",
            WritingStyle = Formatting.WritingStyle.Formal,
        }.ToPortable();

        portable.ContainsKey(nameof(TeezySettings.InputDeviceId)).ShouldBeFalse();
        portable.ContainsKey(nameof(TeezySettings.NumThreads)).ShouldBeFalse();
        portable.ContainsKey(nameof(TeezySettings.ModelPath)).ShouldBeFalse();
        portable.ContainsKey(nameof(TeezySettings.SyncFolder)).ShouldBeFalse();
        portable.ContainsKey(nameof(TeezySettings.WritingStyle)).ShouldBeTrue();
    }

    [Fact]
    public void AnotherComputersChoicesAreTakenAndThisOnesMachineSettingsKept()
    {
        var there = new TeezySettings { WritingStyle = Formatting.WritingStyle.Formal, NumThreads = 8, LlmCleanupEnabled = true };
        var here = new TeezySettings { NumThreads = 2, InputDeviceId = "my-mic" };

        var merged = here.WithPortable(there.ToPortable());

        merged.WritingStyle.ShouldBe(Formatting.WritingStyle.Formal);
        merged.LlmCleanupEnabled.ShouldBeTrue();
        merged.NumThreads.ShouldBe(2);
        merged.InputDeviceId.ShouldBe("my-mic");
    }

    [Fact]
    public void CalendarLinksTravelButSignInsStayWithTheirComputer()
    {
        var there = new TeezySettings { ConnectedAccounts = [Link, SignedIn with { Id = "ms-there" }] };
        var here = new TeezySettings { ConnectedAccounts = [SignedIn] };

        var merged = here.WithPortable(there.ToPortable());

        merged.ConnectedAccounts.Select(a => a.Id).ShouldBe(["ms1", "ics1"], ignoreOrder: true);
    }

    [Fact]
    public void AProfileRoundTrips()
    {
        var profile = new SyncProfile(
            new DateTimeOffset(2026, 9, 22, 9, 30, 0, TimeSpan.FromHours(10)),
            "HOME-PC",
            new TeezySettings { LlmModel = "claude-opus-5" }.ToPortable(),
            new Dictionary<string, string> { ["anthropic-api-key"] = "sk-ant-x" },
            "Pursiva\ncloud code -> Claude Code\n");

        var back = SyncProfile.FromJson(profile.ToJson());

        back.SavedAt.ShouldBe(profile.SavedAt);
        back.SavedBy.ShouldBe("HOME-PC");
        back.Secrets["anthropic-api-key"].ShouldBe("sk-ant-x");
        back.Dictionary.ShouldBe(profile.Dictionary);
        new TeezySettings().WithPortable(back.Settings).LlmModel.ShouldBe("claude-opus-5");
    }
}
