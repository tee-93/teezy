namespace Wisper.Core.Formatting;

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

/// <summary>No-op, for comparing raw engine output against a cleanup pass.</summary>
public sealed class PassthroughFormatter : ITextFormatter
{
    public Task<string> FormatAsync(string raw, CancellationToken ct = default) =>
        Task.FromResult(raw.Trim());
}
