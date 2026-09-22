using Teezy.Core.Voice;

namespace Teezy.Core.Abstractions;

/// <summary>Turns text into audio on this computer, a sentence at a time.</summary>
/// <remarks>
/// Split from playing so the model code — which is cross-platform — stays out of the Windows
/// project, and the Windows project's player stays ignorant of the model.
/// </remarks>
public interface ISpeechSynth : IDisposable
{
    /// <summary>Whether the model's files are on disk.</summary>
    bool IsInstalled { get; }

    /// <summary>Samples per second of what <see cref="Generate"/> hands back.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Synthesises <paramref name="text"/>, handing each sentence's samples to
    /// <paramref name="onAudio"/> as soon as it is ready, so playback can begin after the first.
    /// </summary>
    /// <param name="onAudio">Mono float samples. Return false to stop early.</param>
    /// <remarks>Blocking: call it off the UI thread.</remarks>
    void Generate(string text, KokoroVoice voice, float speed, Func<float[], bool> onAudio, CancellationToken ct);
}
