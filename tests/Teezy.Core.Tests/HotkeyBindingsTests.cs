using Shouldly;
using Teezy.Core.Hotkeys;
using Xunit;

namespace Teezy.Core.Tests;

/// <summary>Watching several combinations at once, and deciding which one is in force.</summary>
public class HotkeyBindingsTests
{
    private static readonly Hotkey CtrlWin = new(HotkeyKey.Control, HotkeyKey.Windows);
    private static readonly Hotkey CtrlAltWin = new(HotkeyKey.Control, HotkeyKey.Alt, HotkeyKey.Windows);
    private static readonly Hotkey F13 = new(HotkeyKey.F13);

    private static HotkeyBindings Bind(params (HotkeyAction Action, Hotkey Hotkey)[] bindings)
    {
        var b = new HotkeyBindings();
        b.Set(bindings.ToDictionary(x => x.Action, x => x.Hotkey));
        return b;
    }

    // ---- one binding ----

    [Fact]
    public void NamesTheBindingThatWasPressed()
    {
        var b = Bind((HotkeyAction.Dictate, CtrlWin));

        b.Update(HotkeyKey.LeftControl, true).IsNothing.ShouldBeTrue();
        b.Update(HotkeyKey.LeftWindows, true).Pressed.ShouldBe(HotkeyAction.Dictate);
        b.Active.ShouldBe(HotkeyAction.Dictate);
    }

    [Fact]
    public void ReleasingNamesTheBindingToo()
    {
        var b = Bind((HotkeyAction.Dictate, CtrlWin));
        b.Update(HotkeyKey.LeftControl, true);
        b.Update(HotkeyKey.LeftWindows, true);

        var change = b.Update(HotkeyKey.LeftWindows, false);

        change.Released.ShouldBe(HotkeyAction.Dictate);
        change.Pressed.ShouldBeNull();
        b.Active.ShouldBeNull();
    }

    // ---- several bindings ----

    [Fact]
    public void DisjointBindingsFireIndependently()
    {
        var b = Bind((HotkeyAction.Dictate, CtrlWin), (HotkeyAction.Assistant, F13));

        b.Update(HotkeyKey.F13, true).Pressed.ShouldBe(HotkeyAction.Assistant);
        b.Update(HotkeyKey.F13, false).Released.ShouldBe(HotkeyAction.Assistant);

        b.Update(HotkeyKey.LeftControl, true);
        b.Update(HotkeyKey.LeftWindows, true).Pressed.ShouldBe(HotkeyAction.Dictate);
    }

    [Fact]
    public void AKeyOutsideEveryBindingChangesNothing()
    {
        var b = Bind((HotkeyAction.Dictate, CtrlWin));

        b.Update(HotkeyKey.ScrollLock, true).IsNothing.ShouldBeTrue();
        b.Active.ShouldBeNull();
    }

    // ---- arbitration ----

    [Fact]
    public void TheMoreSpecificBindingWins()
    {
        // Ctrl+Alt+Win also satisfies Ctrl+Win. Without arbitration both fire and the longer
        // combination becomes impossible to press.
        var b = Bind((HotkeyAction.Dictate, CtrlWin), (HotkeyAction.Assistant, CtrlAltWin));

        b.Update(HotkeyKey.LeftControl, true);
        b.Update(HotkeyKey.LeftAlt, true);

        b.Update(HotkeyKey.LeftWindows, true).Pressed.ShouldBe(HotkeyAction.Assistant);
        b.Active.ShouldBe(HotkeyAction.Assistant);
    }

    [Fact]
    public void AddingAKeyHandsOverFromOneBindingToTheOther()
    {
        // Pressing Ctrl+Win before Alt genuinely does begin dictation — nothing here can see a
        // key that has not been pressed yet. What must not happen is both being held at once,
        // so the handover reports the release and the press together.
        var b = Bind((HotkeyAction.Dictate, CtrlWin), (HotkeyAction.Assistant, CtrlAltWin));

        b.Update(HotkeyKey.LeftControl, true);
        b.Update(HotkeyKey.LeftWindows, true).Pressed.ShouldBe(HotkeyAction.Dictate);

        var change = b.Update(HotkeyKey.LeftAlt, true);

        change.Released.ShouldBe(HotkeyAction.Dictate);
        change.Pressed.ShouldBe(HotkeyAction.Assistant);
    }

    [Fact]
    public void DroppingBackToTheShorterCombinationHandsBack()
    {
        var b = Bind((HotkeyAction.Dictate, CtrlWin), (HotkeyAction.Assistant, CtrlAltWin));
        b.Update(HotkeyKey.LeftControl, true);
        b.Update(HotkeyKey.LeftAlt, true);
        b.Update(HotkeyKey.LeftWindows, true);

        var change = b.Update(HotkeyKey.LeftAlt, false);

        change.Released.ShouldBe(HotkeyAction.Assistant);
        change.Pressed.ShouldBe(HotkeyAction.Dictate);
    }

    // ---- lifecycle ----

    [Fact]
    public void RebindingForgetsWhatWasHeld()
    {
        var b = Bind((HotkeyAction.Dictate, CtrlWin));
        b.Update(HotkeyKey.LeftControl, true);
        b.Update(HotkeyKey.LeftWindows, true);

        b.Set(new Dictionary<HotkeyAction, Hotkey> { [HotkeyAction.Dictate] = F13 });

        b.Active.ShouldBeNull();
        b.Update(HotkeyKey.F13, true).Pressed.ShouldBe(HotkeyAction.Dictate);
    }

    [Fact]
    public void ResetForgetsHeldKeys()
    {
        var b = Bind((HotkeyAction.Dictate, CtrlWin));
        b.Update(HotkeyKey.LeftControl, true);
        b.Update(HotkeyKey.LeftWindows, true);

        b.Reset();

        b.Active.ShouldBeNull();
        // Ctrl alone must not complete it after a reset — the reset cleared both slots.
        b.Update(HotkeyKey.LeftControl, true).IsNothing.ShouldBeTrue();
    }

    [Fact]
    public void AnEmptyCombinationIsNotWatched()
    {
        var b = Bind((HotkeyAction.Dictate, new Hotkey()), (HotkeyAction.Assistant, F13));

        b.Current.Count.ShouldBe(1);
        b.Update(HotkeyKey.F13, true).Pressed.ShouldBe(HotkeyAction.Assistant);
    }

    [Fact]
    public void NothingBoundNeverFires()
    {
        var b = Bind();

        b.Update(HotkeyKey.LeftControl, true).IsNothing.ShouldBeTrue();
        b.Active.ShouldBeNull();
    }

    // ---- overlap detection, for the Settings warning ----

    [Fact]
    public void AContainingCombinationIsDetectable() =>
        CtrlAltWin.Contains(CtrlWin).ShouldBeTrue();

    [Fact]
    public void ADisjointCombinationIsNotContained() =>
        CtrlWin.Contains(F13).ShouldBeFalse();

    [Fact]
    public void TheShorterCombinationDoesNotContainTheLonger() =>
        CtrlWin.Contains(CtrlAltWin).ShouldBeFalse();

    [Fact]
    public void AnEmptyCombinationIsContainedByNothing() =>
        // Otherwise every unbound action would look like an overlap with everything.
        CtrlWin.Contains(new Hotkey()).ShouldBeFalse();
}
