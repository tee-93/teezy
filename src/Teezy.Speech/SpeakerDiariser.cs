using System.Diagnostics;
using SherpaOnnx;
using Teezy.Core.Abstractions;
using Teezy.Core.Meetings;

namespace Teezy.Speech;

/// <summary>Where the two files that tell voices apart live, and whether they are there.</summary>
public sealed record DiariserModels(string Directory, string Segmentation, string Embedding)
{
    /// <summary>Measured sizes of the two downloads; a truncated one must not be used.</summary>
    private static readonly (string Name, long Bytes)[] Expected =
    [
        ("segmentation.onnx", 5_905_000),
        ("speaker.onnx", 29_600_000),
    ];

    private const long SizeTolerance = 2 * 1024 * 1024;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Teezy", "models", "speakers");

    public static DiariserModels For(string directory) => new(
        directory,
        Path.Combine(directory, "segmentation.onnx"),
        Path.Combine(directory, "speaker.onnx"));

    public static DiariserModels Default => For(DefaultDirectory);

    /// <summary>
    /// The two files and where they come from: segmentation decides when someone is talking,
    /// the embedding model decides whether two stretches are the same person. Both are free,
    /// and together they are a fiftieth of the speech model already downloaded.
    /// </summary>
    public static IReadOnlyList<ModelFile> Downloads =>
    [
        new("https://huggingface.co/csukuangfj/sherpa-onnx-pyannote-segmentation-3-0/resolve/main/model.onnx",
            "segmentation.onnx", 5_905_000),
        new("https://huggingface.co/csukuangfj/speaker-embedding-models/resolve/main/3dspeaker_speech_campplus_sv_en_voxceleb_16k.onnx",
            "speaker.onnx", 29_600_000),
    ];

    /// <summary>About 35 MB, for the download button to say so before it starts.</summary>
    public static long DownloadBytes => Downloads.Sum(f => f.Bytes);

    /// <summary>Both files present and the right size.</summary>
    public bool Ready => Expected.All(e =>
        new FileInfo(Path.Combine(Directory, e.Name)) is { Exists: true } file
        && file.Length > e.Bytes - SizeTolerance);
}

/// <summary>
/// Tells the far end's voices apart, so a call with three people in it does not read as one
/// person talking for half an hour.
/// </summary>
/// <remarks>
/// <para>
/// pyannote segmentation plus a speaker-embedding model, both run by the sherpa-onnx already
/// shipping for dictation — no new dependency, two small downloads. It is only ever pointed at
/// the speaker recording: the microphone is the person using the laptop, which needs no model
/// to work out.
/// </para>
/// <para>
/// <b>It works on the whole recording at once</b>, because the voices have to be compared with
/// each other to be grouped, and it holds the audio in memory to do it: an hour at 16 kHz is
/// about 230 MB. Past <see cref="MaxMinutes"/> it declines rather than risking the machine, and
/// the transcript says the far end was left as "Them".
/// </para>
/// </remarks>
public sealed class SpeakerDiariser : IDiariser, IDisposable
{
    /// <summary>Long enough for any real call; beyond it the memory is not worth the labels.</summary>
    private const int MaxMinutes = 75;

    /// <summary>
    /// How alike two stretches must be to be the same person. sherpa-onnx's own example uses
    /// 0.5 when the number of speakers is unknown, which is the normal case here — nobody is
    /// going to tell the app how many people joined the call.
    /// </summary>
    /// <remarks>
    /// Measured here against two similar synthetic voices played through the laptop's speakers:
    /// at 0.5 one person is sometimes split in two, and at 0.7 two people are sometimes counted
    /// as one. Splitting is the better mistake — two labels renamed to the same person merge
    /// into one, while a voice folded into someone else's cannot be got back.
    /// </remarks>
    private const float SameVoice = 0.5f;

    private readonly DiariserModels _models;
    private readonly int _threads;
    private readonly float _sameVoice;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OfflineSpeakerDiarization? _engine;

    public SpeakerDiariser(DiariserModels? models = null, int threads = 2, float sameVoice = SameVoice)
    {
        _models = models ?? DiariserModels.Default;
        _threads = threads;
        _sameVoice = sameVoice;
    }

    public bool IsAvailable => _models.Ready;

    /// <summary>How long the last split took, for the measurements in the README.</summary>
    public TimeSpan LastTook { get; private set; }

    public async Task<IReadOnlyList<SpeakerSpan>> SplitAsync(string wavPath, CancellationToken ct = default)
    {
        if (!IsAvailable || !File.Exists(wavPath)) return [];

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Split(wavPath, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<SpeakerSpan> Split(string wavPath, CancellationToken ct)
    {
        using var reader = new WavReader(wavPath);
        if (reader.Duration > TimeSpan.FromMinutes(MaxMinutes)) return [];

        var samples = reader.Read(0, (int)Math.Min(reader.SampleCount, int.MaxValue));
        if (samples.Length == 0) return [];

        ct.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();

        var engine = _engine ??= Build();
        if (engine.SampleRate != AudioChunk.SampleRate)
        {
            // Everything in this app is 16 kHz; a model wanting another rate is a model we
            // cannot feed, and guessing would produce confident nonsense.
            return [];
        }

        var segments = engine.Process(samples);
        LastTook = Stopwatch.GetElapsedTime(started);

        return [.. segments
            .Select(s => new SpeakerSpan(
                TimeSpan.FromSeconds(s.Start), TimeSpan.FromSeconds(s.End), s.Speaker))
            .OrderBy(s => s.Start)];
    }

    private OfflineSpeakerDiarization Build()
    {
        var config = new OfflineSpeakerDiarizationConfig();
        config.Segmentation.Pyannote.Model = _models.Segmentation;
        config.Segmentation.NumThreads = _threads;
        config.Embedding.Model = _models.Embedding;
        config.Embedding.NumThreads = _threads;

        // Nobody tells the app how many people are on the call, so the voices are grouped by
        // how alike they are rather than into a fixed number of boxes.
        config.Clustering.NumClusters = -1;
        config.Clustering.Threshold = _sameVoice;

        return new OfflineSpeakerDiarization(config);
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _engine = null;
        _gate.Dispose();
    }
}
