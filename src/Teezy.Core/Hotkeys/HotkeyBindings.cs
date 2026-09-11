namespace Teezy.Core.Hotkeys;

/// <summary>What a combination is bound to do.</summary>
/// <remarks>
/// An enum rather than free strings: there will only ever be a handful of these, they are
/// persisted in settings, and a typo in a string would bind a key to nothing at all and say
/// nothing about it.
/// </remarks>
public enum HotkeyAction
{
    /// <summary>Push to talk: transcribe and type what was said.</summary>
    Dictate,

    /// <summary>Push to talk: interpret what was said as a command.</summary>
    Assistant,
}

/// <summary>
/// What one key event did to the set of bindings.
/// </summary>
/// <param name="Released">The binding that stopped being held, if any.</param>
/// <param name="Pressed">The binding that started being held, if any.</param>
/// <remarks>
/// Both can be set by a single event. Holding Ctrl+Win and then adding Alt takes the user from
/// one binding to another without ever letting go, and the consumer has to be told both halves
/// of that or it will believe two things are held at once.
/// </remarks>
public readonly record struct HotkeyChange(HotkeyAction? Released, HotkeyAction? Pressed)
{
    public static HotkeyChange None => default;

    public bool IsNothing => Released is null && Pressed is null;
}

/// <summary>
/// Watches several combinations at once and decides which one is in force.
/// </summary>
/// <remarks>
/// <para>
/// One <see cref="HotkeyMatcher"/> per binding, all fed the same key events. The interesting
/// part is arbitration, because combinations overlap: if dictation is Ctrl+Win and the
/// assistant is Ctrl+Alt+Win, then holding the assistant's keys satisfies both.
/// </para>
/// <para>
/// <b>The most specific complete binding wins</b> — the one with the most keys. Anything else
/// makes the longer combination impossible to press. A consequence worth knowing: pressing
/// Ctrl+Win before Alt genuinely does begin dictation for the few milliseconds before Alt
/// arrives, because no amount of cleverness here can see a key that has not been pressed yet.
/// Dictation's minimum-hold guard discards that, but the chime and the HUD will still flicker,
/// which is why Settings should steer people towards combinations that do not contain one
/// another rather than relying on this to be invisible.
/// </para>
/// </remarks>
public sealed class HotkeyBindings
{
    private readonly Func<HotkeyKey, bool>? _isPhysicallyDown;
    private readonly List<(HotkeyAction Action, HotkeyMatcher Matcher)> _matchers = [];

    private HotkeyAction? _active;

    /// <param name="isPhysicallyDown">
    /// Passed to every matcher. See <see cref="HotkeyMatcher"/> — without it a lost key-up
    /// leaves a slot satisfied for the life of the process.
    /// </param>
    public HotkeyBindings(Func<HotkeyKey, bool>? isPhysicallyDown = null) =>
        _isPhysicallyDown = isPhysicallyDown;

    /// <summary>The binding currently held, if any.</summary>
    public HotkeyAction? Active => _active;

    /// <summary>What is bound, most specific first.</summary>
    public IReadOnlyList<(HotkeyAction Action, Hotkey Hotkey)> Current =>
        [.. _matchers.Select(m => (m.Action, m.Matcher.Hotkey))];

    /// <summary>Replaces every binding. Empty combinations are dropped rather than watched.</summary>
    public void Set(IReadOnlyDictionary<HotkeyAction, Hotkey> bindings)
    {
        _matchers.Clear();

        // Most specific first, so arbitration is a scan that stops at the first complete one.
        // Ties break on the enum value purely so the result is deterministic — two bindings of
        // the same length that are both complete is already a configuration worth warning about.
        var ordered = bindings
            .Where(b => !b.Value.IsEmpty)
            .OrderByDescending(b => b.Value.Keys.Count)
            .ThenBy(b => b.Key);

        foreach (var (action, hotkey) in ordered)
        {
            _matchers.Add((action, new HotkeyMatcher(hotkey, _isPhysicallyDown)));
        }

        _active = null;
    }

    /// <summary>Forgets all held keys. Call whenever the hook is reinstalled.</summary>
    public void Reset()
    {
        foreach (var (_, matcher) in _matchers) matcher.Reset();
        _active = null;
    }

    /// <summary>Feeds one physical key event to every binding.</summary>
    public HotkeyChange Update(HotkeyKey key, bool isDown)
    {
        // Every matcher sees every event, including ones that do not concern it: a matcher
        // that is not told about a key-up would keep believing that key is held.
        foreach (var (_, matcher) in _matchers) matcher.Update(key, isDown);

        HotkeyAction? winner = null;
        foreach (var (action, matcher) in _matchers)
        {
            if (!matcher.IsComplete) continue;
            winner = action;
            break;
        }

        if (winner == _active) return HotkeyChange.None;

        var was = _active;
        _active = winner;
        return new HotkeyChange(was, winner);
    }
}
