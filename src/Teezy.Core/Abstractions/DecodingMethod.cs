namespace Teezy.Core.Abstractions;

/// <summary>How a transducer turns encoder output into text.</summary>
/// <remarks>
/// Lives in Core rather than next to the recogniser because it is a saved setting, and
/// <c>Teezy.Core</c> cannot reference the speech project that consumes it.
/// </remarks>
public enum DecodingMethod
{
    /// <summary>Take the best token at each step. Fastest, and what Teezy shipped with.</summary>
    Greedy = 0,

    /// <summary>
    /// Keep several candidate transcripts alive and pick the best at the end.
    /// </summary>
    /// <remarks>
    /// Slower, better on unusual words — and the <b>only</b> method under which hotwords do
    /// anything, because biasing means re-scoring candidates and greedy decoding keeps none
    /// to re-score.
    /// </remarks>
    BeamSearch = 1,
}
