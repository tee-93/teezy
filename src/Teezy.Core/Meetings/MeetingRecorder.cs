using System.Diagnostics;
using Teezy.Core.Abstractions;

namespace Teezy.Core.Meetings;

/// <summary>Records a meeting: the microphone and the speakers, side by side, to disk.</summary>
/// <remarks>
/// <para>
/// <b>Nothing joins the meeting.</b> This records what this computer hears and plays, exactly
/// as a person at the desk would hear it, so it needs no Teams integration and no one's
/// permission but the user's. It also means no one in the meeting can see it happening, which
/// is why starting it is always an explicit act and never automatic.
/// </para>
/// <para>
/// <b>Recording and transcribing are separate on purpose.</b> On a laptop with two performance
/// cores, running the speech model during a video call risks making the call itself stutter —
/// the worst possible failure in a work meeting. So this only writes audio, which costs almost
/// nothing, and <see cref="MeetingTranscriber"/> runs once the call is over.
/// </para>
/// <para>
/// <b>Losing the speakers is not fatal.</b> A managed laptop may refuse loopback capture; if it
/// does, the microphone is still recorded and <see cref="SpeakersProblem"/> says why the other
/// half is missing. Finding that out is one of the things the first build is for.
/// </para>
/// </remarks>
public sealed class MeetingRecorder : IDisposable
{
    private static readonly long Origin = Stopwatch.GetTimestamp();

    private readonly MeetingStore _store;
    private readonly Func<IAudioCapture> _microphone;
    private readonly Func<IAudioCapture> _speakers;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<DateTimeOffset> _now;

    private readonly Track _me;
    private readonly Track _them;

    private MeetingRecord? _record;
    private TimeSpan _startedAt;

    /// <param name="store">Where meetings are kept.</param>
    /// <param name="microphone">Makes a fresh capture of the microphone for each meeting.</param>
    /// <param name="speakers">Makes a fresh capture of what the speakers are playing.</param>
    /// <param name="clock">A monotonic clock. Tests pass their own.</param>
    /// <param name="now">The wall clock, for naming the meeting.</param>
    public MeetingRecorder(
        MeetingStore store,
        Func<IAudioCapture> microphone,
        Func<IAudioCapture> speakers,
        Func<TimeSpan>? clock = null,
        Func<DateTimeOffset>? now = null)
    {
        _store = store;
        _microphone = microphone;
        _speakers = speakers;
        _clock = clock ?? (() => Stopwatch.GetElapsedTime(Origin));
        _now = now ?? (() => DateTimeOffset.Now);
        _me = new Track(Side.Me, this);
        _them = new Track(Side.Them, this);
    }

    /// <summary>A smoothed 0..1 level for either side, for the meters. Raised on capture threads.</summary>
    public event Action<Side, float>? LevelChanged;

    public bool IsRecording => _record is not null;

    public MeetingRecord? Current => _record;

    public TimeSpan Elapsed => IsRecording ? _clock() - _startedAt : TimeSpan.Zero;

    public string? MicrophoneName => _me.DeviceName;

    public string? SpeakersName => _them.DeviceName;

    /// <summary>Why the speakers are not being recorded, or null if they are.</summary>
    public string? SpeakersProblem { get; private set; }

    /// <exception cref="AudioCaptureException">The microphone could not be opened.</exception>
    public MeetingRecord Start()
    {
        if (_record is not null) throw new InvalidOperationException("A meeting is already being recorded.");

        var record = _store.Create(_now());
        _startedAt = _clock();
        SpeakersProblem = null;

        try
        {
            _me.Open(_microphone(), record.MePath);
        }
        catch (AudioCaptureException)
        {
            _me.Close(deleteFile: true);
            _store.Delete(record);
            throw;
        }

        try
        {
            _them.Open(_speakers(), record.ThemPath);
        }
        catch (AudioCaptureException e)
        {
            _them.Close(deleteFile: true);
            SpeakersProblem = e.Message;
        }

        _record = record;
        return record;
    }

    /// <summary>Stops both recordings and saves what is known about the meeting.</summary>
    public MeetingRecord Stop()
    {
        var record = _record ?? throw new InvalidOperationException("No meeting is being recorded.");
        var recorded = Elapsed;

        _me.Close(deleteFile: false);
        _them.Close(deleteFile: false);
        _record = null;

        var finished = record with
        {
            Info = record.Info with
            {
                Recorded = recorded,
                MeDevice = _me.DeviceName,
                ThemDevice = _them.DeviceName,
                ThemProblem = SpeakersProblem,
                MeHeard = _me.Heard,
                ThemHeard = _them.Heard,
            },
        };

        _store.Save(finished);
        return finished;
    }

    public void Dispose()
    {
        if (IsRecording) Stop();
    }

    /// <summary>One side of the meeting: a capture, its file, and the aligner between them.</summary>
    private sealed class Track(Side side, MeetingRecorder owner)
    {
        private readonly object _gate = new();
        private IAudioCapture? _capture;
        private WavWriter? _writer;
        private StreamAligner? _aligner;
        private string? _path;

        public string? DeviceName { get; private set; }

        public bool Heard { get; private set; }

        public void Open(IAudioCapture capture, string path)
        {
            _capture = capture;
            _path = path;
            DeviceName = null;
            Heard = false;

            lock (_gate)
            {
                _writer = new WavWriter(path);
                _aligner = new StreamAligner(() => owner._clock() - owner._startedAt);
            }

            capture.ChunkAvailable += OnChunk;
            capture.LevelChanged += OnLevel;
            capture.Start();
            DeviceName = capture.DeviceName;
        }

        private void OnChunk(AudioChunk chunk)
        {
            lock (_gate)
            {
                if (_writer is null || _aligner is null) return;

                var silence = _aligner.SilenceBefore(chunk.Samples.Length);
                if (silence > 0) _writer.WriteSilence(silence);
                _writer.Write(chunk.Samples);
            }
        }

        private void OnLevel(float level) => owner.LevelChanged?.Invoke(side, level);

        public void Close(bool deleteFile)
        {
            if (_capture is { } capture)
            {
                capture.Stop();
                capture.ChunkAvailable -= OnChunk;
                capture.LevelChanged -= OnLevel;
                Heard = capture.SawSignal;
                capture.Dispose();
                _capture = null;
            }

            // Under the same lock as the writes: a capture thread can still deliver one last
            // buffer after Stop returns, and it must find no writer rather than a closed one.
            lock (_gate)
            {
                _writer?.Dispose();
                _writer = null;
                _aligner = null;
            }

            if (deleteFile && _path is not null) File.Delete(_path);
        }
    }
}
