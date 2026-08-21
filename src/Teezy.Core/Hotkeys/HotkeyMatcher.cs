namespace Teezy.Core.Hotkeys;

/// <summary>What a key event did to the combination.</summary>
public enum HotkeyTransition
{
    None,

    /// <summary>Every key in the combination is now held.</summary>
    Pressed,

    /// <summary>The combination was complete and no longer is.</summary>
    Released,
}

/// <summary>Tracks whether a whole key combination is currently held.</summary>
/// <remarks>
/// <para>
/// Platform-neutral on purpose: the keyboard hook is untestable, but this is where the
/// interesting behaviour lives — press order, auto-repeat, partial release, and a key held
/// on both sides of the keyboard at once.
/// </para>
/// <para>
/// Each slot in the combination tracks <i>which</i> physical keys are satisfying it, not
/// merely whether one is. Otherwise holding both Ctrl keys and releasing one would report the
/// combination as broken while a Ctrl is still physically down.
/// </para>
/// </remarks>
public sealed class HotkeyMatcher
{
    private readonly List<HashSet<HotkeyKey>> _satisfiedBy = [];

    private Hotkey _hotkey = new();
    private bool _isComplete;

    public HotkeyMatcher(Hotkey? hotkey = null) => Hotkey = hotkey ?? new Hotkey();

    public Hotkey Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            Reset();
        }
    }

    /// <summary>True while every key in the combination is held.</summary>
    public bool IsComplete => _isComplete;

    /// <summary>
    /// Forgets all held keys.
    /// </summary>
    /// <remarks>
    /// Must be called whenever the hook is reinstalled or the combination changes. Key-up
    /// events that arrive while the hook is down are lost forever, and without a reset the
    /// matcher would believe a key is still held and never fire again.
    /// </remarks>
    public void Reset()
    {
        _satisfiedBy.Clear();
        for (var i = 0; i < _hotkey.Keys.Count; i++) _satisfiedBy.Add([]);
        _isComplete = false;
    }

    /// <summary>Feeds one physical key event.</summary>
    /// <param name="key">The specific key, e.g. <see cref="HotkeyKey.LeftControl"/>.</param>
    /// <param name="isDown">True for key-down, false for key-up.</param>
    public HotkeyTransition Update(HotkeyKey key, bool isDown)
    {
        if (_hotkey.IsEmpty) return HotkeyTransition.None;

        var touched = false;

        for (var i = 0; i < _hotkey.Keys.Count; i++)
        {
            if (!HotkeyKeys.Satisfies(_hotkey.Keys[i], key)) continue;

            touched = true;
            if (isDown) _satisfiedBy[i].Add(key);
            else _satisfiedBy[i].Remove(key);
        }

        // A key outside the combination changes nothing. Notably it does NOT break an active
        // hold: the user may well type or click while dictating.
        if (!touched) return HotkeyTransition.None;

        var complete = _satisfiedBy.TrueForAll(s => s.Count > 0);

        // Auto-repeat resends key-down while held. Only edges are reported, or the controller
        // would be told "pressed" dozens of times a second for the whole hold.
        if (complete == _isComplete) return HotkeyTransition.None;

        _isComplete = complete;
        return complete ? HotkeyTransition.Pressed : HotkeyTransition.Released;
    }
}
