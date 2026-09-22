using NAudio.Wave;
using Teezy.Core.Abstractions;
using Teezy.Core.Voice;

namespace Teezy.Platform.Windows;

/// <summary>Reads answers in a Kokoro voice, starting as soon as the first sentence is ready.</summary>
/// <remarks>
/// Synthesis runs on a worker thread and feeds a buffer the sound card is already playing
/// from, so the first sentence is heard while the second is still being made. Stop cancels
/// both: the synthesiser gives up at its next sentence, and the device goes quiet at once.
/// </remarks>
public sealed class KokoroSpeaker(ISpeechSynth synth) : ISpeaker
{
    private readonly object _gate = new();
    private CancellationTokenSource? _playing;
    private WaveOutEvent? _device;

    /// <summary>Slightly quicker than Kokoro's natural pace, which reads as unhurried for answers.</summary>
    private const float Speed = 1.05f;

    public bool IsAvailable => synth.IsInstalled;

    public IReadOnlyList<SpeechVoice> Voices() =>
        synth.IsInstalled ? [.. KokoroVoices.All.Select(v => v.ToSpeechVoice())] : [];

    public string? VoiceListError => synth.IsInstalled ? null : "Download the natural voices first.";

    public string? PreferredVoice { get; set; }

    public string? VoiceName => synth.IsInstalled ? Describe(KokoroVoices.Find(PreferredVoice)) : null;

    private static string Describe(KokoroVoice voice) => $"{voice.Name} ({(voice.British ? "British" : "American")})";

    public Task SpeakAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !synth.IsInstalled) return Task.CompletedTask;

        Stop();

        CancellationTokenSource cts;
        BufferedWaveProvider buffer;
        WaveOutEvent device;
        lock (_gate)
        {
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _playing = cts;

            // A minute of room: an answer is a few sentences, and the synthesiser runs ahead.
            buffer = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(synth.SampleRate, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(90),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };

            device = new WaveOutEvent { DesiredLatency = 200 };
            device.Init(buffer);
            _device = device;
        }

        var voice = KokoroVoices.Find(PreferredVoice);
        return Task.Run(() =>
        {
            var started = false;
            try
            {
                synth.Generate(text, voice, Speed, samples =>
                {
                    if (cts.IsCancellationRequested) return false;
                    var bytes = new byte[samples.Length * sizeof(float)];
                    Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
                    buffer.AddSamples(bytes, 0, bytes.Length);

                    if (!started)
                    {
                        started = true;
                        lock (_gate) { if (_device == device) device.Play(); }
                    }

                    return true;
                }, cts.Token);

                // Let what is buffered finish, then free the device.
                while (!cts.IsCancellationRequested && buffer.BufferedDuration > TimeSpan.Zero)
                {
                    Thread.Sleep(100);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or NAudio.MmException or ObjectDisposedException)
            {
                // The answer is already on screen. Losing the voice is not worth an error.
            }
            finally
            {
                lock (_gate)
                {
                    if (_device == device)
                    {
                        Quiet(device);
                        _device = null;
                    }
                }
            }
        }, CancellationToken.None);
    }

    public void Stop()
    {
        lock (_gate)
        {
            _playing?.Cancel();
            _playing = null;
            if (_device is { } device) Quiet(device);
            _device = null;
        }
    }

    private static void Quiet(WaveOutEvent device)
    {
        try
        {
            device.Stop();
            device.Dispose();
        }
        catch (Exception e) when (e is InvalidOperationException or NAudio.MmException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Stop();
        synth.Dispose();
    }
}
