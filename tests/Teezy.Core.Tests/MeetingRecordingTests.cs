using Shouldly;
using Teezy.Core.Abstractions;
using Teezy.Core.Meetings;
using Xunit;

namespace Teezy.Core.Tests;

public sealed class MeetingRecordingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "teezy-meetings-" + Guid.NewGuid().ToString("N"));
    private readonly MeetingStore _store;
    private readonly FakeCapture _mic = new() { SawSignal = true };
    private readonly FakeCapture _speakers = new() { SawSignal = true };
    private TimeSpan _now;

    public MeetingRecordingTests() => _store = new MeetingStore(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private MeetingRecorder Recorder() => new(
        _store, () => _mic, () => _speakers, () => _now,
        () => new DateTimeOffset(2026, 9, 13, 9, 30, 0, TimeSpan.FromHours(10)));

    /// <summary>A second of tone, loud enough to be speech.</summary>
    private static float[] Tone(double seconds, float amplitude = 0.2f)
    {
        var samples = new float[(int)(seconds * AudioChunk.SampleRate)];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = amplitude * MathF.Sin(2 * MathF.PI * 220 * i / AudioChunk.SampleRate);
        }

        return samples;
    }

    [Fact]
    public void Both_sides_are_recorded_to_disk_and_the_meeting_is_described()
    {
        var recorder = Recorder();
        var record = recorder.Start();

        _now = TimeSpan.FromMilliseconds(100);
        _mic.Emit(1600);
        _speakers.Emit(1600);

        _now = TimeSpan.FromSeconds(42);
        var finished = recorder.Stop();

        File.Exists(record.MePath).ShouldBeTrue();
        File.Exists(record.ThemPath).ShouldBeTrue();
        recorder.IsRecording.ShouldBeFalse();

        var saved = _store.List().ShouldHaveSingleItem();
        saved.Info.Recorded.ShouldBe(TimeSpan.FromSeconds(42));
        saved.Info.MeDevice.ShouldBe("fake");
        saved.Info.ThemDevice.ShouldBe("fake");
        saved.Info.MeHeard.ShouldBeTrue();
        finished.Info.ThemProblem.ShouldBeNull();
    }

    [Fact]
    public void Speakers_that_stay_quiet_do_not_pull_their_words_earlier()
    {
        var recorder = Recorder();
        var record = recorder.Start();

        _now = TimeSpan.FromMilliseconds(100);
        _speakers.Emit(1600);

        // Loopback delivers nothing while nothing plays, then resumes.
        _now = TimeSpan.FromSeconds(3);
        _speakers.Emit(1600);
        recorder.Stop();

        using var reader = new WavReader(record.ThemPath);
        reader.SampleCount.ShouldBe(3 * AudioChunk.SampleRate);
    }

    [Fact]
    public void Speakers_that_cannot_be_opened_still_leave_the_microphone_recording()
    {
        _speakers.FailOnStart = true;
        var recorder = Recorder();

        var record = recorder.Start();
        recorder.SpeakersProblem.ShouldBe("no device");
        _mic.IsRunning.ShouldBeTrue();

        var finished = recorder.Stop();
        File.Exists(record.ThemPath).ShouldBeFalse();
        finished.Info.ThemProblem.ShouldBe("no device");
    }

    [Fact]
    public void A_microphone_that_cannot_be_opened_leaves_nothing_behind()
    {
        _mic.FailOnStart = true;

        Should.Throw<AudioCaptureException>(() => Recorder().Start());
        _store.List().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_recorded_meeting_is_transcribed_in_order_and_its_audio_deleted()
    {
        var recorder = Recorder();
        var record = recorder.Start();

        // Me: 2 s quiet, 3 s talking, 6 s quiet. Them: 6 s quiet, 3 s talking, 2 s quiet.
        _mic.Emit(new float[2 * AudioChunk.SampleRate]);
        _mic.Emit(Tone(3));
        _mic.Emit(new float[6 * AudioChunk.SampleRate]);
        _speakers.Emit(new float[6 * AudioChunk.SampleRate]);
        _speakers.Emit(Tone(3));
        _speakers.Emit(new float[2 * AudioChunk.SampleRate]);

        _now = TimeSpan.FromSeconds(11);
        record = recorder.Stop();

        var model = new ScriptedTranscriber("Good morning everyone.", "Thanks for joining.");
        var reported = new List<MeetingProgress>();

        var done = await new MeetingTranscriber(model, _store)
            .TranscribeAsync(record, new SynchronousProgress(reported.Add));

        model.Calls.ShouldBe(2);
        done.HasAudio.ShouldBeFalse();
        done.Info.Stats.ShouldNotBeNull().Pieces.ShouldBe(2);
        done.Info.Stats.Recorded.ShouldBe(TimeSpan.FromSeconds(11));
        reported[^1].Fraction.ShouldBe(1);

        var text = await File.ReadAllTextAsync(done.TranscriptPath);
        text.IndexOf("Me: Good morning everyone.", StringComparison.Ordinal)
            .ShouldBeLessThan(text.IndexOf("Them: Thanks for joining.", StringComparison.Ordinal));

        _store.List().ShouldHaveSingleItem().Info.Stats.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_meeting_where_the_speakers_were_refused_says_so_in_the_transcript()
    {
        _speakers.FailOnStart = true;
        var recorder = Recorder();
        recorder.Start();
        _mic.Emit(Tone(2));
        _now = TimeSpan.FromSeconds(2);
        var record = recorder.Stop();

        var done = await new MeetingTranscriber(new ScriptedTranscriber("Hello."), _store).TranscribeAsync(record);

        (await File.ReadAllTextAsync(done.TranscriptPath)).ShouldContain("Windows would not let TeezyFlow hear the speakers: no device");
    }

    [Fact]
    public void Meetings_are_listed_newest_first_and_broken_folders_skipped()
    {
        _store.Create(new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        _store.Create(new DateTimeOffset(2026, 9, 12, 9, 0, 0, TimeSpan.Zero));
        Directory.CreateDirectory(Path.Combine(_root, "not a meeting"));

        var listed = _store.List();

        listed.Count.ShouldBe(2);
        listed[0].Info.Started.Day.ShouldBe(12);
    }

    [Fact]
    public void Two_meetings_started_in_the_same_second_get_their_own_folders()
    {
        var at = DateTimeOffset.Now;

        _store.Create(at).Folder.ShouldNotBe(_store.Create(at).Folder);
    }

    private sealed class ScriptedTranscriber(params string[] lines) : ITranscriber
    {
        public event Action<string>? PartialAvailable { add { } remove { } }

        public int Calls { get; private set; }

        public bool IsLoaded => true;

        public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken ct = default) =>
            Task.FromResult(lines[Math.Min(Calls++, lines.Length - 1)]);

        public void Dispose() { }
    }

    /// <summary>Progress&lt;T&gt; posts to a context; tests want the reports in hand when the await returns.</summary>
    private sealed class SynchronousProgress(Action<MeetingProgress> report) : IProgress<MeetingProgress>
    {
        public void Report(MeetingProgress value) => report(value);
    }
}
