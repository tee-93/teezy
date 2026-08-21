using System.Diagnostics;
using SherpaOnnx;
using Teezy.Core.Abstractions;

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
    private readonly int _threads;
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

    public ParakeetTranscriber(string? modelDirectory = null, int threads = 4)
    {
        _configuredDirectory = modelDirectory;
        _threads = threads;
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
            cfg.ModelConfig.NumThreads = _threads;
            cfg.ModelConfig.Provider = "cpu";
            cfg.DecodingMethod = "greedy_search";

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
