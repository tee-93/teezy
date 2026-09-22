using Teezy.Core.Abstractions;

namespace Teezy.Core.Voice;

/// <summary>One Kokoro voice: its name in the model, and the speaker number it is stored under.</summary>
/// <param name="Id">Kokoro's own name, e.g. <c>bf_emma</c>: accent, gender, name.</param>
/// <param name="Speaker">Its index in <c>voices.bin</c> for Kokoro v1.0.</param>
public sealed record KokoroVoice(string Id, int Speaker, string Name, bool British, bool Female)
{
    public SpeechVoice ToSpeechVoice() =>
        new(Id, Name, British ? "en-GB" : "en-US", Female ? "Female" : "Male", IsModern: true);
}

/// <summary>The English voices in Kokoro v1.0 worth offering, British first.</summary>
/// <remarks>
/// The model carries 54 voices across nine languages; only the English ones are listed, since
/// the assistant answers in English and a Japanese voice reading English is not a feature.
/// Speaker numbers are fixed by the model's <c>voices.bin</c>, in Kokoro's own order.
/// </remarks>
public static class KokoroVoices
{
    public static readonly IReadOnlyList<KokoroVoice> All =
    [
        new("bf_emma", 21, "Emma", British: true, Female: true),
        new("bf_isabella", 22, "Isabella", British: true, Female: true),
        new("bf_alice", 20, "Alice", British: true, Female: true),
        new("bf_lily", 23, "Lily", British: true, Female: true),
        new("bm_george", 26, "George", British: true, Female: false),
        new("bm_lewis", 27, "Lewis", British: true, Female: false),
        new("bm_daniel", 24, "Daniel", British: true, Female: false),
        new("bm_fable", 25, "Fable", British: true, Female: false),
        new("af_heart", 3, "Heart", British: false, Female: true),
        new("af_bella", 2, "Bella", British: false, Female: true),
        new("af_nicole", 6, "Nicole", British: false, Female: true),
        new("af_sarah", 9, "Sarah", British: false, Female: true),
        new("af_sky", 10, "Sky", British: false, Female: true),
        new("am_michael", 16, "Michael", British: false, Female: false),
        new("am_adam", 11, "Adam", British: false, Female: false),
        new("am_eric", 13, "Eric", British: false, Female: false),
        new("am_liam", 15, "Liam", British: false, Female: false),
        new("am_onyx", 17, "Onyx", British: false, Female: false),
    ];

    /// <summary>What automatic means: a British voice, the nearest Kokoro has to Australian.</summary>
    public static KokoroVoice Default => All[0];

    public static KokoroVoice Find(string? id) => All.FirstOrDefault(v => v.Id == id) ?? Default;
}
