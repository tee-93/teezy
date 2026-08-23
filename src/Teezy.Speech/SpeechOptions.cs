using Teezy.Core.Abstractions;

namespace Teezy.Speech;

/// <summary>Recogniser settings that are fixed when the model loads.</summary>
/// <remarks>
/// Everything here is read once, in <c>LoadAsync</c>, because sherpa-onnx bakes it into the
/// recogniser. Changing any of it means rebuilding the recogniser and paying the load again —
/// which is why the settings page says these take effect on restart, rather than pretending
/// a picker can change them mid-session. Hotword <i>text</i> is the exception and is passed
/// per utterance; only the score lives here.
/// </remarks>
public sealed record SpeechOptions
{
    /// <summary>
    /// Threads for the ONNX session. More is not better past a point — the encoder is one
    /// graph and oversubscribing makes it slower.
    /// </summary>
    public int Threads { get; init; } = 4;

    public DecodingMethod Decoding { get; init; } = DecodingMethod.Greedy;

    /// <summary>Candidates kept alive by beam search. Ignored under greedy decoding.</summary>
    public int BeamSize { get; init; } = 4;

    /// <summary>
    /// How hard a dictionary hint pulls the decoder toward its spelling.
    /// </summary>
    /// <remarks>
    /// Not a free lunch: bias high enough and the engine starts hearing your hinted words in
    /// audio that does not contain them, which is a worse failure than the misspelling it was
    /// meant to fix — a name appearing where you never said it is harder to spot than one
    /// spelled wrong.
    /// </remarks>
    public float HotwordScore { get; init; } = 1.5f;

    public string SherpaDecodingMethod =>
        Decoding == DecodingMethod.BeamSearch ? "modified_beam_search" : "greedy_search";
}
