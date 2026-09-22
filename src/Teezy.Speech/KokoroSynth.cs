using System.Runtime.InteropServices;
using SherpaOnnx;
using Teezy.Core.Abstractions;
using Teezy.Core.Voice;

namespace Teezy.Speech;

/// <summary>Kokoro through sherpa-onnx: text in, a sentence of audio at a time out.</summary>
/// <remarks>
/// <para>
/// <b>A sentence at a time</b> is what makes it usable. Measured on the Snapdragon X Plus:
/// about 1.2 s to the first sentence, then faster than it plays (roughly 0.8 s of work per
/// second of speech), so starting playback after the first sentence means no gaps — where
/// waiting for the whole answer meant sitting through four or five seconds of silence first.
/// </para>
/// <para>
/// <b>Four threads.</b> Six was no faster and eight was markedly slower on the same machine;
/// dictation's recogniser also wants the cores, and the two can overlap.
/// </para>
/// <para>
/// The model is loaded on first use and kept, one per accent: the lexicon — British or
/// American pronunciation — is fixed when the model is built, so changing between a British
/// and an American voice builds the other one once.
/// </para>
/// </remarks>
public sealed class KokoroSynth(KokoroModel model) : ISpeechSynth
{
    private const int Threads = 4;

    private readonly object _gate = new();
    private OfflineTts? _tts;
    private bool _ttsBritish;

    public bool IsInstalled => model.IsInstalled;

    public int SampleRate => 24_000;

    public void Generate(string text, KokoroVoice voice, float speed, Func<float[], bool> onAudio, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || !model.IsInstalled) return;

        // One at a time: the model is not safe to drive from two threads, and a new answer
        // replaces the old one anyway.
        lock (_gate)
        {
            var tts = Load(voice.British);

            OfflineTtsCallback callback = (IntPtr samples, int count) =>
            {
                if (ct.IsCancellationRequested) return 0;
                var chunk = new float[count];
                Marshal.Copy(samples, chunk, 0, count);
                return onAudio(chunk) && !ct.IsCancellationRequested ? 1 : 0;
            };

            tts.GenerateWithCallback(text, speed, voice.Speaker, callback);
            GC.KeepAlive(callback);
        }
    }

    private OfflineTts Load(bool british)
    {
        if (_tts is not null && _ttsBritish == british) return _tts;

        _tts?.Dispose();
        var config = new OfflineTtsConfig();
        config.Model.Kokoro.Model = Path.Combine(model.Directory, KokoroModel.ModelFile);
        config.Model.Kokoro.Voices = Path.Combine(model.Directory, KokoroModel.VoicesFile);
        config.Model.Kokoro.Tokens = Path.Combine(model.Directory, KokoroModel.TokensFile);
        config.Model.Kokoro.DataDir = Path.Combine(model.Directory, KokoroModel.DataFolder);
        config.Model.Kokoro.Lexicon = Path.Combine(model.Directory,
            british ? KokoroModel.BritishLexicon : KokoroModel.AmericanLexicon);
        config.Model.NumThreads = Threads;
        config.Model.Provider = "cpu";
        config.Model.Debug = 0;
        config.MaxNumSentences = 1;

        _tts = new OfflineTts(config);
        _ttsBritish = british;
        return _tts;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _tts?.Dispose();
            _tts = null;
        }
    }
}
