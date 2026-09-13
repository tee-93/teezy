using Shouldly;
using Teezy.Core.Meetings;
using Xunit;

namespace Teezy.Core.Tests;

public sealed class MeetingAudioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "teezy-wav-" + Guid.NewGuid().ToString("N"));

    public MeetingAudioTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string File(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Audio_comes_back_as_it_was_written()
    {
        var path = File("a.wav");
        float[] written = [0f, 0.5f, -0.5f, 0.25f, -1f];

        using (var writer = new WavWriter(path)) writer.Write(written);

        using var reader = new WavReader(path);
        reader.SampleCount.ShouldBe(written.Length);

        var read = reader.Read(0, written.Length);
        for (var i = 0; i < written.Length; i++) read[i].ShouldBe(written[i], 0.001f);
    }

    [Fact]
    public void Reads_can_start_anywhere_and_stop_at_the_end()
    {
        var path = File("b.wav");
        using (var writer = new WavWriter(path)) writer.Write([0.1f, 0.2f, 0.3f, 0.4f]);

        using var reader = new WavReader(path);
        var tail = reader.Read(2, 100);

        tail.Length.ShouldBe(2);
        tail[0].ShouldBe(0.3f, 0.001f);
        reader.Read(10, 5).ShouldBeEmpty();
    }

    [Fact]
    public void Silence_counts_as_written()
    {
        var path = File("c.wav");
        using (var writer = new WavWriter(path))
        {
            writer.WriteSilence(40_000);
            writer.Write([0.5f]);
            writer.SamplesWritten.ShouldBe(40_001);
        }

        using var reader = new WavReader(path);
        reader.SampleCount.ShouldBe(40_001);
        reader.Read(39_999, 2)[1].ShouldBe(0.5f, 0.001f);
    }

    [Fact]
    public void A_recording_interrupted_before_its_header_was_finished_is_still_readable()
    {
        var path = File("d.wav");
        using (var writer = new WavWriter(path)) writer.Write(new float[1000]);

        // What a crash leaves behind: the sizes never patched from zero.
        var bytes = System.IO.File.ReadAllBytes(path);
        Array.Clear(bytes, 4, 4);
        Array.Clear(bytes, 40, 4);
        System.IO.File.WriteAllBytes(path, bytes);

        using var reader = new WavReader(path);
        reader.SampleCount.ShouldBe(1000);
    }

    [Fact]
    public void Samples_past_full_scale_are_clamped_not_wrapped()
    {
        var path = File("e.wav");
        using (var writer = new WavWriter(path)) writer.Write([1.8f, -3f]);

        using var reader = new WavReader(path);
        var read = reader.Read(0, 2);
        read[0].ShouldBeGreaterThan(0.99f);
        read[1].ShouldBeLessThan(-0.99f);
    }

    [Fact]
    public void Something_that_is_not_a_wav_file_is_refused()
    {
        var path = File("f.wav");
        System.IO.File.WriteAllText(path, "definitely not audio, but long enough to read a header from");

        Should.Throw<InvalidDataException>(() => new WavReader(path));
    }

    [Fact]
    public void Steady_capture_gets_no_padding()
    {
        var now = TimeSpan.Zero;
        var aligner = new StreamAligner(() => now);

        for (var i = 1; i <= 10; i++)
        {
            now = TimeSpan.FromMilliseconds(100 * i);
            aligner.SilenceBefore(1600).ShouldBe(0);
        }

        aligner.Position.ShouldBe(16_000);
    }

    [Fact]
    public void A_gap_in_delivery_is_filled_with_the_silence_it_stood_for()
    {
        var now = TimeSpan.Zero;
        var aligner = new StreamAligner(() => now);

        for (var i = 1; i <= 5; i++)
        {
            now = TimeSpan.FromMilliseconds(100 * i);
            aligner.SilenceBefore(1600);
        }

        now = TimeSpan.FromSeconds(3);
        aligner.SilenceBefore(1600).ShouldBe(48_000 - 8_000 - 1_600);
        aligner.Position.ShouldBe(48_000);
    }

    [Fact]
    public void A_stream_that_only_starts_playing_later_begins_at_the_right_time()
    {
        var now = TimeSpan.FromSeconds(1);
        var aligner = new StreamAligner(() => now);

        aligner.SilenceBefore(1600).ShouldBe(16_000 - 1_600);
    }

    [Fact]
    public void Ordinary_buffering_jitter_is_not_mistaken_for_a_gap()
    {
        var now = TimeSpan.FromMilliseconds(100);
        var aligner = new StreamAligner(() => now);
        aligner.SilenceBefore(1600);

        now = TimeSpan.FromMilliseconds(300);
        aligner.SilenceBefore(1600).ShouldBe(0);
    }
}
