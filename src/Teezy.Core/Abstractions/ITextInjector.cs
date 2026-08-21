namespace Teezy.Core.Abstractions;

/// <summary>Puts finished text into whatever window currently has keyboard focus.</summary>
public interface ITextInjector
{
    /// <summary>Types <paramref name="text"/> into the focused control.</summary>
    /// <returns>How the text was delivered, for logging and diagnostics.</returns>
    InjectionResult Insert(string text);
}

public enum InjectionResult
{
    /// <summary>Nothing to insert.</summary>
    Skipped,

    /// <summary>Synthesised keystrokes carrying the characters directly.</summary>
    Typed,

    /// <summary>Clipboard + Ctrl+V, with the previous clipboard contents restored.</summary>
    Pasted,

    /// <summary>No focused window accepted the text.</summary>
    Failed,
}
