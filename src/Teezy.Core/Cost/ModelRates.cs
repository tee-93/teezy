namespace Teezy.Core.Cost;

/// <summary>What a model cost per million tokens, over a period.</summary>
public sealed record ModelRate(
    string Model,
    decimal InputPerMTok,
    decimal OutputPerMTok,
    DateOnly From,
    DateOnly? Until = null)
{
    public bool Covers(DateOnly day) => day >= From && (Until is null || day <= Until);

    public decimal Cost(TokenUsage usage) =>
        (usage.InputTokens * InputPerMTok + usage.OutputTokens * OutputPerMTok) / 1_000_000m;
}

/// <summary>Published prices, with the dates they applied.</summary>
/// <remarks>
/// <para>
/// <b>Dated on purpose.</b> Sonnet 5 is running an introductory rate that ends on
/// 2026-08-31, after which it goes back up by half. A single hardcoded number would quietly
/// under-report every dictation from September onwards, and re-pricing old history at the new
/// rate would be just as wrong in the other direction — so each entry is costed against the
/// rate that applied on the day it was spoken.
/// </para>
/// <para>
/// This table is a copy of published pricing and will go stale. When it does not know a model
/// or a date, it returns null and the UI shows tokens without a figure, which is the honest
/// failure: no number beats a wrong one when it is someone's money.
/// </para>
/// <para>
/// Prices are Anthropic first-party API rates as published on 2026-06-24.
/// </para>
/// </remarks>
public static class ModelRates
{
    private static readonly DateOnly Epoch = new(2000, 1, 1);

    private static readonly ModelRate[] Rates =
    [
        // Sonnet 5 — introductory pricing, then standard.
        new("claude-sonnet-5", 2.00m, 10.00m, Epoch, new DateOnly(2026, 8, 31)),
        new("claude-sonnet-5", 3.00m, 15.00m, new DateOnly(2026, 9, 1)),

        new("claude-haiku-4-5", 1.00m, 5.00m, Epoch),
        new("claude-opus-5", 5.00m, 25.00m, Epoch),
        new("claude-sonnet-4-6", 3.00m, 15.00m, Epoch),
    ];

    /// <summary>The cost of one call in US dollars, or null if the price is not known.</summary>
    public static decimal? Cost(string? model, TokenUsage? usage, DateOnly on)
    {
        if (model is null || usage is null || usage.IsEmpty) return null;

        var rate = Rates.FirstOrDefault(r =>
            string.Equals(r.Model, model, StringComparison.OrdinalIgnoreCase) && r.Covers(on));

        return rate?.Cost(usage);
    }

    /// <summary>True if every model named is one this table can price.</summary>
    public static bool KnowsAll(IEnumerable<string> models) =>
        models.All(m => Rates.Any(r => string.Equals(r.Model, m, StringComparison.OrdinalIgnoreCase)));
}
