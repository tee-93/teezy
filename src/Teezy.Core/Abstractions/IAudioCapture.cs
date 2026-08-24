namespace Teezy.Core.Abstractions;

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

    /// <summary>
    /// The device to open, by <see cref="AudioDevice.Id"/>. Null follows the Windows default.
    /// </summary>
    /// <remarks>
    /// Read at <see cref="Start"/> rather than applied immediately, so changing it mid-utterance
    /// cannot swap the microphone out from under a recording in progress. The next hold uses
    /// the new device.
    /// </remarks>
    string? PreferredDeviceId { get; set; }

    /// <summary>
    /// True when <see cref="PreferredDeviceId"/> named a device that could not be opened and
    /// the system default was used instead.
    /// </summary>
    /// <remarks>
    /// A chosen microphone that has been unplugged must not mean silence. Falling back keeps
    /// dictation working; this flag is how the app can still say that it did.
    /// </remarks>
    bool UsingFallbackDevice { get; }

    /// <summary>True if any sample since <see cref="Start"/> was non-zero.</summary>
    /// <remarks>
    /// The one thing a level meter cannot tell you on its own. A blocked microphone and a
    /// quiet room both read as an empty meter, but only one of them is digital zero — so this
    /// is what lets the app say "Windows is not letting Teezy hear you" instead of leaving
    /// someone speaking louder and louder at a bar that will never move.
    /// </remarks>
    bool SawSignal { get; }

    /// <summary>Microphones currently present, for the settings picker.</summary>
    /// <remarks>Safe to call at any time, started or not.</remarks>
    IReadOnlyList<AudioDevice> Devices();
}

public sealed class AudioCaptureException(string message, Exception? inner = null)
    : Exception(message, inner);
