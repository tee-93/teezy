using System.Diagnostics;
using SherpaOnnx;
using Teezy.Core.Abstractions;
using Teezy.Core.Speech;

namespace Teezy.Speech;

/// <summary>NVIDIA Parakeet TDT 0.6B (int8 ONNX) via sherpa-onnx.</summary>
/// <remarks>
/// <para>
/// Every config value below was verified against a real transcription on this machine.
/// Three of them are non-obvious and each costs an hour if missed:
/// <c>ModelType = "nemo_transducer"</c> is mandatory, <c>FeatureDim</c> is <b>128</b> rather
/// than the default 80, and the recogniser must be given absolute paths that exist —
/// see the null-handle note in <see cref="LoadAsync"/>.
/// </para>
/// <para>
/// <b>CPU only, deliberately.</b> sherpa-onnx ships no GPU package; the DirectML runtime
/// forbids the variable-length tensor shapes this model needs; and CUDA would force a
/// toolkit install on every user. Measured on this Snapdragon X Plus at int8: 7.4 s of audio
/// transcribes in 250 ms with 4 threads — 30x realtime. 8 threads measured *slower*.
/// </para>
/// </remarks>
public sealed class ParakeetTranscriber : ITranscriber
{
    /// <summary>
    /// The exported encoder carries a fixed relative-position table sized for 5000 frames at
    /// 80 ms each. Past that, inference <i>fails</i> rather than degrading, with a broadcast
    /// error naming an axis mismatch. Dictation never comes close; a long recording would.
    /// </summary>
    private const int MaxSeconds = 380;

    private readonly string? _configuredDirectory;
    private SpeechOptions _options;
    private readonly Func<string?> _hotwords;
    private OfflineRecognizer? _recognizer;

    /// <summary>sherpa-onnx is not documented as thread-safe for concurrent decode on one
    /// recogniser, and utterances are strictly sequential anyway.</summary>
    private readonly SemaphoreSlim _decodeGate = new(1, 1);

#pragma warning disable CS0067 // Batch engine: there is no partial transcript to raise.
    public event Action<string>? PartialAvailable;
#pragma warning restore CS0067

    public bool IsLoaded => _recognizer is not null;
    public ModelPaths? Paths { get; private set; }
    public TimeSpan LoadTime { get; private set; }

    /// <param name="hotwords">
    /// Words to bias toward, newline-separated. Read when the recogniser is built, not per
    /// utterance — sherpa-onnx's C# binding has no per-stream hotwords for offline models —
    /// so it is a provider rather than a value, and <see cref="ReloadAsync"/> is what makes
    /// an edited dictionary take effect.
    /// </param>
    public ParakeetTranscriber(
        string? modelDirectory = null,
        SpeechOptions? options = null,
        Func<string?>? hotwords = null)
    {
        _configuredDirectory = modelDirectory;
        _options = options ?? new SpeechOptions();
        _hotwords = hotwords ?? (() => null);
    }

    public Task LoadAsync(CancellationToken ct = default)
    {
        if (_recognizer is not null) return Task.CompletedTask;

        // Resolve and validate *before* touching sherpa-onnx. This is the important part:
        // the native constructor does not throw on a bad config. It writes a message to
        // stderr and hands back a recogniser that misbehaves later, far from the cause.
        // Verified experimentally — empty paths produced an object, not an exception.
        var paths = ModelPaths.Resolve(_configuredDirectory);
        if (paths is null)
        {
            var attempted = ModelPaths.For(
                ModelPaths.SearchPath(_configuredDirectory).First());
            throw new TranscriberException(
                "Parakeet model not found.\n\n"
                + string.Join('\n', attempted.Validate())
                + $"\n\nExpected in: {attempted.Directory}");
        }

        return Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();

            var cfg = new OfflineRecognizerConfig();
            cfg.FeatConfig.SampleRate = AudioChunk.SampleRate;
            cfg.FeatConfig.FeatureDim = 128;                    // NOT the default 80
            cfg.ModelConfig.Transducer.Encoder = paths.Encoder;
            cfg.ModelConfig.Transducer.Decoder = paths.Decoder;
            cfg.ModelConfig.Transducer.Joiner = paths.Joiner;
            cfg.ModelConfig.Tokens = paths.Tokens;
            cfg.ModelConfig.ModelType = "nemo_transducer";      // REQUIRED — omit and load fails
            cfg.ModelConfig.NumThreads = _options.Threads;
            cfg.ModelConfig.Provider = "cpu";
            cfg.DecodingMethod = _options.SherpaDecodingMethod;
            cfg.MaxActivePaths = _options.BeamSize;
            cfg.HotwordsScore = _options.HotwordScore;

            // Hotwords belong to the recogniser, not the stream: the C# binding exposes no
            // per-stream overload for offline models, so changing the hints means rebuilding.
            // Only under beam search — greedy decoding keeps no alternatives to re-score, and
            // sherpa-onnx silently ignores hotwords rather than saying so.
            if (_options.Decoding == DecodingMethod.BeamSearch &&
                _hotwords() is { Length: > 0 } words)
            {
                var vocabulary = HotwordEncoder.LoadVocabulary(paths.Tokens);
                var encoded = HotwordEncoder.Encode(
                    words.Split('\n', StringSplitOptions.RemoveEmptyEntries), vocabulary);

                if (encoded is not null) cfg.HotwordsFile = WriteHotwords(paths.Directory, encoded);
            }

            try
            {
                _recognizer = new OfflineRecognizer(cfg);
            }
            catch (Exception e)
            {
                throw new TranscriberException($"Could not load the Parakeet model: {e.Message}", e);
            }

            Paths = paths;
            LoadTime = sw.Elapsed;
        }, ct);
    }

    /// <summary>
    /// Rebuilds the recogniser so edited hints take effect.
    /// </summary>
    /// <remarks>
    /// Costs a full model load, which is why it is not called after every keystroke in the
    /// dictionary page. Cheap enough to call when the file is saved, and the alternative —
    /// hints that only work after a restart — is the kind of thing nobody discovers and
    /// everybody assumes is broken.
    /// </remarks>
    /// <summary>The settings the recogniser was last built with.</summary>
    public SpeechOptions Options => _options;

    /// <summary>
    /// Rebuilds with different settings — the only way to change any of them, since
    /// sherpa-onnx bakes them into the recogniser.
    /// </summary>
    /// <remarks>
    /// Reusing this instance rather than standing a second one up beside it is deliberate:
    /// the model is about 900 MB resident, and benchmarking thread counts by holding two
    /// recognisers at once would nearly double that on machines least able to afford it.
    /// </remarks>
    public Task ReloadAsync(SpeechOptions options, CancellationToken ct = default)
    {
        _options = options;
        return ReloadAsync(ct);
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _recognizer?.Dispose();
            _recognizer = null;
        }
        finally
        {
            _decodeGate.Release();
        }

        await LoadAsync(ct).ConfigureAwait(false);
    }

    /// <summary>sherpa-onnx reads hotwords from a file path, so there has to be a file.</summary>
    private static string WriteHotwords(string directory, string words)
    {
        var path = Path.Combine(directory, "hotwords.txt");
        File.WriteAllText(path, words.ReplaceLineEndings("\n") + "\n");
        return path;
    }

    public async Task<string> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken ct = default)
    {
        var recognizer = _recognizer
            ?? throw new TranscriberException("Transcribe called before the model finished loading.");

        if (samples.Length == 0) return string.Empty;

        var max = MaxSeconds * AudioChunk.SampleRate;
        if (samples.Length > max) samples = samples[..max];

        await _decodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                using var stream = recognizer.CreateStream();
                stream.AcceptWaveform(AudioChunk.SampleRate, samples.ToArray());
                recognizer.Decode(stream);
                return stream.Result.Text?.Trim() ?? string.Empty;
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            _decodeGate.Release();
        }
    }

    public void Dispose()
    {
        _recognizer?.Dispose();
        _recognizer = null;
        _decodeGate.Dispose();
    }
}
