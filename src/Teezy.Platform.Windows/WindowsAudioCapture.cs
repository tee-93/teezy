using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Teezy.Core.Abstractions;

namespace Teezy.Platform.Windows;

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

    /// <summary>Identifier of the device actually opened, which is not always the one asked for.</summary>
    public string? DeviceId { get; private set; }

    public string? PreferredDeviceId { get; set; }

    public bool UsingFallbackDevice { get; private set; }

    /// <summary>True if any sample since <see cref="Start"/> was non-zero.</summary>
    public bool SawSignal { get; private set; }

    /// <summary>Active capture endpoints, with the Windows choice marked.</summary>
    /// <remarks>
    /// Only <see cref="DeviceState.Active"/> devices are listed. Windows also reports
    /// unplugged and disabled endpoints, and offering those would let someone pick a
    /// microphone that cannot record — a setting that looks applied and produces nothing.
    /// </remarks>
    public IReadOnlyList<AudioDevice> Devices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();

            string? defaultId = null;
            if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Communications))
            {
                using var preferred = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                defaultId = preferred.ID;
            }

            // The collection itself is not disposable in NAudio 2.3.0; the devices in it are.
            var devices = new List<AudioDevice>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device)
                {
                    devices.Add(new AudioDevice(device.ID, device.FriendlyName, device.ID == defaultId));
                }
            }

            return devices;
        }
        catch (COMException)
        {
            // A machine with no audio stack at all still has to render a settings page.
            return [];
        }
    }

    public void Start()
    {
        Stop();
        SawSignal = false;
        UsingFallbackDevice = false;

        try
        {
            using var enumerator = new MMDeviceEnumerator();

            _device = OpenPreferred(enumerator) ?? OpenDefault(enumerator);
            DeviceName = _device.FriendlyName;
            DeviceId = _device.ID;

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

    /// <summary>The chosen device, or null to fall back — never an exception.</summary>
    /// <remarks>
    /// A microphone that has been unplugged since it was chosen is an ordinary Tuesday, not a
    /// failure worth refusing to dictate over. <see cref="UsingFallbackDevice"/> records that
    /// it happened so Settings can say so, rather than silently appearing to honour a choice
    /// it is not honouring.
    /// </remarks>
    private MMDevice? OpenPreferred(MMDeviceEnumerator enumerator)
    {
        if (PreferredDeviceId is not { Length: > 0 } id) return null;

        try
        {
            var device = enumerator.GetDevice(id);
            if (device.State == DeviceState.Active) return device;

            device.Dispose();
        }
        catch (Exception e) when (e is COMException or ArgumentException)
        {
            // Gone entirely: the id refers to hardware that is no longer on this machine.
        }

        UsingFallbackDevice = true;
        return null;
    }

    // Role.Communications, not Role.Console: Windows lets the user nominate a
    // different default specifically for voice, and dictation is exactly that.
    private static MMDevice OpenDefault(MMDeviceEnumerator enumerator) =>
        enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

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
