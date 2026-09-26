namespace Teezy.Core.Commands;

/// <summary>Which transport key to send.</summary>
public enum MediaKey
{
    PlayPause,
    Next,
    Previous,
}

/// <summary>
/// Something the assistant can actually do, as a closed set.
/// </summary>
/// <remarks>
/// <para>
/// A type per capability, with typed parameters, rather than a string anyone could execute.
/// That is the whole security posture of this feature in one decision: when the LLM tier
/// arrives it will <i>choose from</i> these, never name a command of its own, so the worst a
/// confused model can do is set the volume to the wrong number.
/// </para>
/// <para>
/// Deliberately absent: closing windows, killing processes, anything that deletes. Speech
/// recognition is wrong often enough that irreversible actions are the wrong pairing, and the
/// list is meant to stay short until the rest is solid.
/// </para>
/// </remarks>
public abstract record VoiceCommand
{
    /// <summary>Open, or focus if already running.</summary>
    /// <param name="Query">
    /// What the user called it, not a resolved path. Matching a name to something installed is
    /// the platform's job — Core has no idea what is on this machine.
    /// </param>
    public sealed record LaunchApp(string Query) : VoiceCommand;

    /// <summary>Move the volume by a number of percentage points, positive or negative.</summary>
    public sealed record AdjustVolume(int Delta) : VoiceCommand;

    /// <summary>Set the volume to an absolute percentage, already clamped to 0–100.</summary>
    public sealed record SetVolume(int Percent) : VoiceCommand;

    public sealed record Mute(bool On) : VoiceCommand;

    public sealed record Media(MediaKey Key) : VoiceCommand;

    public sealed record LockScreen : VoiceCommand;

    /// <summary>Add a task to TeezyFlow's own list. Reversible with a tick, so safe to offer the model.</summary>
    /// <param name="Title">What to do.</param>
    /// <param name="When">When, in the user's words — "friday 2pm", "tomorrow" — read by <see cref="Tasks.TaskInput"/>; empty for no date.</param>
    public sealed record AddTask(string Title, string When) : VoiceCommand;

    /// <param name="Amount">The value as the user said it — "four thousand two hundred", "$4,200".</param>
    public sealed record AddQuote(string Customer, string Amount, string What) : VoiceCommand;
}
