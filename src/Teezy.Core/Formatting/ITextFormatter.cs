namespace Teezy.Core.Formatting;

/// <summary>The cleanup pass between raw transcription and injection.</summary>
/// <remarks>
/// This is the seam where dictation stops feeling like a transcript and starts feeling like
/// writing. It is an interface so an LLM-backed tier can be dropped in later without the
/// controller or anything downstream changing.
/// </remarks>
public interface ITextFormatter
{
    /// <param name="raw">Engine output, exactly as produced.</param>
    Task<string> FormatAsync(string raw, CancellationToken ct = default);
}

/// <summary>A formatter that can say what its last run consumed.</summary>
/// <remarks>
/// Separate from <see cref="ITextFormatter"/> rather than folded into it: most formatters are
/// local and free, and giving every one of them a token count would be inventing a concept
/// that only the paid tier has. The controller asks whether the formatter it was handed
/// happens to implement this, and records nothing when it does not.
/// </remarks>
public interface IReportsUsage
{
    /// <summary>Tokens the most recent call consumed, or null if none was made.</summary>
    Cost.TokenUsage? LastTokens { get; }

    /// <summary>The model that call went to.</summary>
    string? LastModel { get; }
}

/// <summary>No-op, for comparing raw engine output against a cleanup pass.</summary>
public sealed class PassthroughFormatter : ITextFormatter
{
    public Task<string> FormatAsync(string raw, CancellationToken ct = default) =>
        Task.FromResult(raw.Trim());
}
