namespace Teezy.Core.Cost;

/// <summary>What one API call consumed.</summary>
/// <remarks>
/// <para>
/// Taken from the <c>usage</c> block the API returns on every response, so these are counted
/// by the thing doing the billing rather than estimated locally. There is no endpoint that
/// reports account spend to an ordinary API key — the Console has that — but this is the more
/// useful number anyway: it is what <b>Teezy</b> costs, not what the whole account costs.
/// </para>
/// <para>
/// Cache figures are recorded but not priced. Teezy's system prompt is far below the ~1024
/// token minimum for a cacheable prefix, so in practice they are zero; storing them anyway
/// means the day that changes, the data is already there to notice it.
/// </para>
/// </remarks>
public sealed record TokenUsage(
    int InputTokens,
    int OutputTokens,
    int CacheReadTokens = 0,
    int CacheWriteTokens = 0)
{
    public static readonly TokenUsage None = new(0, 0);

    public int Total => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    public bool IsEmpty => Total == 0;

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) => new(
        a.InputTokens + b.InputTokens,
        a.OutputTokens + b.OutputTokens,
        a.CacheReadTokens + b.CacheReadTokens,
        a.CacheWriteTokens + b.CacheWriteTokens);
}
