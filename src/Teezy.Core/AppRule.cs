using Teezy.Core.Formatting;

namespace Teezy.Core;

/// <summary>A writing style that applies only in one app.</summary>
/// <remarks>
/// <para>
/// The thing every review of every competitor singles out: a Slack message wants a different
/// register from an email, and having to remember to change a global setting before typing
/// each one is worse than not having the setting.
/// </para>
/// <para>
/// Matched on process name, exactly and case-insensitively, with any <c>.exe</c> ignored.
/// Substring matching was the obvious alternative and is the wrong call — a rule for "code"
/// silently capturing "vscode" and "qbittorrent-code" is the kind of behaviour nobody can
/// predict from the list they are looking at.
/// </para>
/// </remarks>
public sealed record AppRule
{
    /// <summary>Process name, as it appears in history — "OUTLOOK", "Cursor", "slack".</summary>
    public required string App { get; init; }

    public WritingStyle Style { get; init; } = WritingStyle.Faithful;

    /// <summary>An extra line for this app only, replacing the global one.</summary>
    public string? Instruction { get; init; }

    /// <summary>Off keeps the rule in the list without applying it, so a rule can be tried
    /// and put aside without retyping it.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Case-insensitive, ignoring a trailing <c>.exe</c> on either side.</summary>
    public bool Matches(string? app)
    {
        if (string.IsNullOrWhiteSpace(app) || string.IsNullOrWhiteSpace(App)) return false;
        return string.Equals(Strip(app), Strip(App), StringComparison.OrdinalIgnoreCase);
    }

    private static string Strip(string name)
    {
        var trimmed = name.Trim();
        return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^4]
            : trimmed;
    }
}
