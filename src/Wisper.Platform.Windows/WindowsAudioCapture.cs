using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Wisper.Core.Abstractions;

namespace Wisper.Platform.Windows;

/// <summary>Microphone capture through WASAPI, delivered as 16 kHz mono float32.</summary>
/// <remarks>
/// <para>
/// The device is opened <i>directly</i> at Parakeet's format rather than at the hardware mix
/// format with a resampler behind it. WASAPI shared mode performs the conversion itself —
/// verified on this machine, whose array microphone runs natively at 48 kHz stereo — which
/// removes an entire resampling stage and the drift and buffering questions that come with
/// it.
/// </para>
/// <para>
/// <b>Silence rather than an error is the signature of a permissions problem.</b> When
/// "Let desktop apps access your microphone" is off, WASAPI opens the device and returns
/// digital zeroes forever. Nothing throws. <see cref="SawSignal"/> exists so the app can
/// tell the user that specific truth instead of showing an empty transcript.
/// </para>
/// </remarks>
public sealed class WindowsAudioCapture : IAudioCapture
{
    /// <summary>Anything above this counts as real signal rather than a dead channel.</summary>
    private const float SilenceFloor = 1e-5f;

    private WasapiCapture? _capture;
    private MMDevice? _device;

    public event Action<AudioChunk>? ChunkAvailable;
    public event Action<float>? LevelChanged;

    public string? DeviceName { get; private set; }

    /// <summary>True if any sample since <see cref="Start"/> was non-zero.</summary>
    public bool SawSignal { get; private set; }

    public void Start()
    {
        Stop();
        SawSignal = false;

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            // Role.Communications, not Role.Console: Windows lets the user nominate a
            // different default specifically for voice, and dictation is exactly that.
            _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            DeviceName = _device.FriendlyName;

            _capture = new WasapiCapture(_device)
            {
                WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(AudioChunk.SampleRate, 1),
            };
            _capture.DataAvailable += OnData;
            _capture.StartRecording();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            Stop();
            throw new AudioCaptureException(
                $"Could not open the microphone: {e.Message}", e);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var count = e.BytesRecorded / sizeof(float);
        if (count == 0) return;

        // A fresh array every time, never a view over NAudio's buffer: that buffer is reused
        // the moment this handler returns, so a chunk that borrowed it would be rewritten
        // underneath the consumer. The symptom is a garbled transcript under load, not a
        // crash, which makes it very hard to trace back to here.
        var samples = new float[count];
        Buffer.BlockCopy(e.Buffer, 0, samples, 0, count * sizeof(float));

        double sumOfSquares = 0;
        foreach (var s in samples) sumOfSquares += s * s;
        var rms = Math.Sqrt(sumOfSquares / count);

        if (rms > SilenceFloor) SawSignal = true;

        ChunkAvailable?.Invoke(new AudioChunk(samples));

        LevelChanged?.Invoke(AudioLevel.ToMeter(rms));
    }

    public void Stop()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnData;
            try { _capture.StopRecording(); } catch (COMException) { /* device already gone */ }
            _capture.Dispose();
            _capture = null;
        }

        _device?.Dispose();
        _device = null;
    }

    public void Dispose() => Stop();
}
