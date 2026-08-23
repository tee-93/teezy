namespace Teezy.Core.Formatting;

/// <summary>How much licence the cleanup pass has to change your words.</summary>
/// <remarks>
/// <para>
/// A spectrum, not a set of personalities. Every option still rewrites <i>your</i> sentence —
/// none of them are allowed to answer it, add content, or change what you meant. What varies
/// is how far the wording may move from what you actually said.
/// </para>
/// <para>
/// This only affects the Claude tier. The offline rules do one thing and are not tunable,
/// which is also why they stay the floor: whatever the style, a failed or implausible call
/// falls back to text that reads like you.
/// </para>
/// </remarks>
public enum WritingStyle
{
    /// <summary>Fix the transcript, leave the writing alone. The default.</summary>
    Faithful = 0,

    /// <summary>Tighten waffle and fix awkward phrasing, keeping the voice.</summary>
    Polished = 1,

    /// <summary>Professional register — the version you would send to someone senior.</summary>
    Formal = 2,

    /// <summary>Relaxed register, contractions, shorter sentences.</summary>
    Casual = 3,
}
