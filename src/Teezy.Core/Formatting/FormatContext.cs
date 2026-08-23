namespace Teezy.Core.Formatting;

/// <summary>What the cleanup pass is allowed to know about where the text is going.</summary>
/// <remarks>
/// Deliberately thin. It carries the process name Teezy already reads for the history entry
/// and nothing else — not the window title, not what is on screen. Some competitors read the
/// screen to infer context; that buys a little accuracy and gives away the property this app
/// is built on.
/// </remarks>
public readonly record struct FormatContext(string? App)
{
    public static readonly FormatContext None = new((string?)null);
}

/// <summary>The style settled on for one utterance.</summary>
public readonly record struct CleanupStyle(WritingStyle Style, string? Instruction);
