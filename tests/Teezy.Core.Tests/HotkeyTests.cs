using Shouldly;
using Teezy.Core.Hotkeys;
using Xunit;

namespace Teezy.Core.Tests;

public class HotkeyTests
{
    [Fact]
    public void DisplayIsOrderedRegardlessOfHowItWasBuilt()
    {
        // A hotkey is read as a fixed phrase. One that renders differently depending on the
        // order it was pressed in looks like a bug.
        new Hotkey(HotkeyKey.Windows, HotkeyKey.Control).Display.ShouldBe("Ctrl + Win");
        new Hotkey(HotkeyKey.Control, HotkeyKey.Windows).Display.ShouldBe("Ctrl + Win");
    }

    [Fact]
    public void DuplicateKeysCollapse() =>
        new Hotkey(HotkeyKey.Control, HotkeyKey.Control).Keys.Count.ShouldBe(1);

    [Fact]
    public void EqualityComparesContentsNotListIdentity() =>
        // Records compare the list property by reference by default, which would make every
        // saved hotkey look different from every loaded one.
        new Hotkey(HotkeyKey.Control, HotkeyKey.Windows)
            .ShouldBe(new Hotkey(HotkeyKey.Windows, HotkeyKey.Control));

    [Fact]
    public void IsCappedSoItStaysHoldable() =>
        new Hotkey(HotkeyKey.Control, HotkeyKey.Alt, HotkeyKey.Shift, HotkeyKey.Windows,
                   HotkeyKey.ScrollLock).Keys.Count.ShouldBe(Hotkey.MaxKeys);

    // ---- warnings ----

    [Fact]
    public void WarnsThatShiftTriggersFilterKeys() =>
        // Holding Shift for eight seconds opens the Filter Keys prompt, and push-to-talk
        // holds routinely run longer than that.
        new Hotkey(HotkeyKey.RightShift).Warnings
            .ShouldContain(w => w.Contains("Filter Keys", StringComparison.Ordinal));

    [Fact]
    public void WarnsThatLoneWindowsKeyOpensStartMenu() =>
        new Hotkey(HotkeyKey.Windows).Warnings
            .ShouldContain(w => w.Contains("Start menu", StringComparison.Ordinal));

    [Fact]
    public void DefaultCombinationHasNoWarnings() =>
        // Ctrl+Win is the default precisely because it is clean: no character, no system
        // action, and Ctrl suppresses the Start menu that Win alone would open.
        Hotkey.Default.Warnings.ShouldBeEmpty();

    [Fact]
    public void WarnsThatPlainControlFiresOnCopyAndPaste() =>
        new Hotkey(HotkeyKey.Control).Warnings
            .ShouldContain(w => w.Contains("Ctrl+C", StringComparison.Ordinal));

    [Fact]
    public void WarnsAboutAltGr() =>
        new Hotkey(HotkeyKey.Control, HotkeyKey.RightAlt).Warnings
            .ShouldContain(w => w.Contains("AltGr", StringComparison.Ordinal));

    [Fact]
    public void EmptyHotkeyWarnsItCannotFire() =>
        new Hotkey().Warnings.ShouldNotBeEmpty();
}

public class HotkeyMatcherTests
{
    private static HotkeyMatcher CtrlWin() =>
        new(new Hotkey(HotkeyKey.Control, HotkeyKey.Windows));

    [Fact]
    public void FiresOnlyWhenEveryKeyIsHeld()
    {
        var m = CtrlWin();
        m.Update(HotkeyKey.LeftControl, true).ShouldBe(HotkeyTransition.None);
        m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.Pressed);
        m.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void PressOrderDoesNotMatter()
    {
        var m = CtrlWin();
        m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.None);
        m.Update(HotkeyKey.RightControl, true).ShouldBe(HotkeyTransition.Pressed);
    }

    [Fact]
    public void ReleasingAnyKeyEndsTheHold()
    {
        var m = CtrlWin();
        m.Update(HotkeyKey.LeftControl, true);
        m.Update(HotkeyKey.LeftWindows, true);
        m.Update(HotkeyKey.LeftControl, false).ShouldBe(HotkeyTransition.Released);
        m.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void AutoRepeatDoesNotRefire()
    {
        var m = CtrlWin();
        m.Update(HotkeyKey.LeftControl, true);
        m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.Pressed);

        for (var i = 0; i < 20; i++)
        {
            m.Update(HotkeyKey.LeftControl, true).ShouldBe(HotkeyTransition.None);
            m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.None);
        }
    }

    [Fact]
    public void HoldingBothSidesAndReleasingOneKeepsTheHold()
    {
        // Both Ctrl keys down, release one: a Ctrl is still physically held, so dictation
        // must not stop mid-sentence.
        var m = CtrlWin();
        m.Update(HotkeyKey.LeftControl, true);
        m.Update(HotkeyKey.RightControl, true);
        m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.Pressed);

        m.Update(HotkeyKey.LeftControl, false).ShouldBe(HotkeyTransition.None);
        m.IsComplete.ShouldBeTrue();

        m.Update(HotkeyKey.RightControl, false).ShouldBe(HotkeyTransition.Released);
    }

    [Fact]
    public void TypingWhileDictatingDoesNotBreakTheHold()
    {
        var m = CtrlWin();
        m.Update(HotkeyKey.LeftControl, true);
        m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.Pressed);

        m.Update(HotkeyKey.ScrollLock, true).ShouldBe(HotkeyTransition.None);
        m.Update(HotkeyKey.ScrollLock, false).ShouldBe(HotkeyTransition.None);
        m.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void SideSpecificCombinationIgnoresTheOtherSide()
    {
        var m = new HotkeyMatcher(new Hotkey(HotkeyKey.RightControl));
        m.Update(HotkeyKey.LeftControl, true).ShouldBe(HotkeyTransition.None);
        m.Update(HotkeyKey.RightControl, true).ShouldBe(HotkeyTransition.Pressed);
    }

    [Fact]
    public void CanBeHeldAgainAfterRelease()
    {
        var m = CtrlWin();
        for (var round = 0; round < 3; round++)
        {
            m.Update(HotkeyKey.LeftControl, true);
            m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.Pressed);
            m.Update(HotkeyKey.LeftWindows, false).ShouldBe(HotkeyTransition.Released);
            m.Update(HotkeyKey.LeftControl, false).ShouldBe(HotkeyTransition.None);
        }
    }

    [Fact]
    public void ResetForgetsHeldKeys()
    {
        // Key-up events that arrive while the hook is uninstalled are lost forever. Without
        // a reset the matcher would think a key is still down and never fire again.
        var m = CtrlWin();
        m.Update(HotkeyKey.LeftControl, true);
        m.Reset();

        m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.None);
        m.Update(HotkeyKey.LeftControl, true).ShouldBe(HotkeyTransition.Pressed);
    }

    [Fact]
    public void ChangingTheHotkeyResetsState()
    {
        var m = CtrlWin();
        m.Update(HotkeyKey.LeftControl, true);

        m.Hotkey = new Hotkey(HotkeyKey.ScrollLock);
        m.IsComplete.ShouldBeFalse();
        m.Update(HotkeyKey.ScrollLock, true).ShouldBe(HotkeyTransition.Pressed);
    }

    [Fact]
    public void AnEmptyHotkeyNeverFires() =>
        new HotkeyMatcher(new Hotkey()).Update(HotkeyKey.LeftControl, true)
            .ShouldBe(HotkeyTransition.None);

    [Fact]
    public void ThreeKeyCombinationNeedsAllThree()
    {
        var m = new HotkeyMatcher(new Hotkey(HotkeyKey.Control, HotkeyKey.Alt, HotkeyKey.Windows));
        m.Update(HotkeyKey.LeftControl, true).ShouldBe(HotkeyTransition.None);
        m.Update(HotkeyKey.LeftAlt, true).ShouldBe(HotkeyTransition.None);
        m.Update(HotkeyKey.RightWindows, true).ShouldBe(HotkeyTransition.Pressed);
        m.Update(HotkeyKey.LeftAlt, false).ShouldBe(HotkeyTransition.Released);
    }

    // ---- missed key-up ----

    [Fact]
    public void ALostKeyUpDoesNotLeaveTheHotkeyFiringOnOneKey()
    {
        // Reported from real use: Settings showed "Ctrl + Win" and dictation began on Ctrl
        // alone. A key-up can simply never arrive — a low-level hook receives nothing while
        // an elevated window has focus, and the hook is also evicted if a callback runs long.
        // The slot stays satisfied forever, so every later Ctrl completes the combination.
        var held = new HashSet<HotkeyKey> { HotkeyKey.LeftControl, HotkeyKey.LeftWindows };
        var m = new HotkeyMatcher(new Hotkey(HotkeyKey.Control, HotkeyKey.Windows), held.Contains);

        m.Update(HotkeyKey.LeftControl, true);
        m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.Pressed);

        // Win is physically released, but its key-up never reaches us.
        held.Remove(HotkeyKey.LeftWindows);

        m.Update(HotkeyKey.LeftControl, false).ShouldBe(HotkeyTransition.Released);
        held.Remove(HotkeyKey.LeftControl);

        // Ctrl on its own must not dictate.
        held.Add(HotkeyKey.LeftControl);
        m.Update(HotkeyKey.LeftControl, true).ShouldBe(HotkeyTransition.None);
        m.IsComplete.ShouldBeFalse();
    }

    [Fact]
    public void AKeyStillPhysicallyHeldKeepsSatisfyingItsSlot()
    {
        // The other half of the same fix: reconciling against the keyboard must not punish
        // someone who legitimately keeps Win held down between two dictations.
        var held = new HashSet<HotkeyKey> { HotkeyKey.LeftWindows };
        var m = new HotkeyMatcher(new Hotkey(HotkeyKey.Control, HotkeyKey.Windows), held.Contains);

        m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.None);

        held.Add(HotkeyKey.LeftControl);
        m.Update(HotkeyKey.LeftControl, true).ShouldBe(HotkeyTransition.Pressed);

        held.Remove(HotkeyKey.LeftControl);
        m.Update(HotkeyKey.LeftControl, false).ShouldBe(HotkeyTransition.Released);

        // Win never left the keyboard, so pressing Ctrl again starts a second dictation.
        held.Add(HotkeyKey.LeftControl);
        m.Update(HotkeyKey.LeftControl, true).ShouldBe(HotkeyTransition.Pressed);
    }

    [Fact]
    public void TheKeyThisEventIsAboutIsTrustedOverTheKeyboard()
    {
        // Shipped broken in 1.5.2 and reported as "not triggering at all".
        //
        // A low-level keyboard hook runs BEFORE Windows updates the async key state, so
        // during the key-down that completes a combination the keyboard still reports that
        // key as up. Reconciling it away removes the key that has just arrived, and the
        // hotkey can never fire. The event is the authority on its own key; the keyboard is
        // the authority on every other one.
        var held = new HashSet<HotkeyKey>();
        var m = new HotkeyMatcher(new Hotkey(HotkeyKey.Control, HotkeyKey.Windows), held.Contains);

        m.Update(HotkeyKey.LeftControl, true).ShouldBe(HotkeyTransition.None);

        // State catches up only once that event has been processed.
        held.Add(HotkeyKey.LeftControl);

        m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.Pressed);
        m.IsComplete.ShouldBeTrue();
    }

    [Fact]
    public void AKeyDownStillCannotReviveAStaleSlot()
    {
        // The exemption is narrow on purpose: it covers only the key this event carries, so
        // a slot left stale by a lost key-up is still corrected by the very next event.
        var held = new HashSet<HotkeyKey> { HotkeyKey.LeftControl, HotkeyKey.LeftWindows };
        var m = new HotkeyMatcher(new Hotkey(HotkeyKey.Control, HotkeyKey.Windows), held.Contains);

        m.Update(HotkeyKey.LeftControl, true);
        m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.Pressed);

        held.Remove(HotkeyKey.LeftWindows);          // released, key-up never delivered
        m.Update(HotkeyKey.LeftControl, false).ShouldBe(HotkeyTransition.Released);
        held.Remove(HotkeyKey.LeftControl);

        held.Add(HotkeyKey.LeftControl);
        m.Update(HotkeyKey.LeftControl, true).ShouldBe(HotkeyTransition.None);
    }

    [Fact]
    public void WithoutAProbeBehaviourIsUnchanged()
    {
        // The probe is optional so the matcher stays platform-neutral and every existing
        // test still describes the same machine.
        var m = CtrlWin();
        m.Update(HotkeyKey.LeftControl, true).ShouldBe(HotkeyTransition.None);
        m.Update(HotkeyKey.LeftWindows, true).ShouldBe(HotkeyTransition.Pressed);
    }
}
