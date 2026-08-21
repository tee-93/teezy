namespace Wisper.Core.Abstractions;

/// <summary>Microphone capture, normalised to <see cref="AudioChunk"/>.</summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Raised for each captured block, in capture order.</summary>
    event Action<AudioChunk>? ChunkAvailable;

    /// <summary>Raised with a smoothed 0..1 level, for the HUD meter.</summary>
    event Action<float>? LevelChanged;

    /// <summary>Opens the device and begins capturing.</summary>
    /// <exception cref="AudioCaptureException">The device could not be opened.</exception>
    void Start();

    /// <summary>Stops capture. Safe to call when not started.</summary>
    void Stop();

    /// <summary>Friendly name of the device in use, for Settings. Null before <see cref="Start"/>.</summary>
    string? DeviceName { get; }
}

public sealed class AudioCaptureException(string message, Exception? inner = null)
    : Exception(message, inner);
