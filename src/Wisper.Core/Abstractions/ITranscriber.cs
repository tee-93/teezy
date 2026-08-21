namespace Wisper.Core.Abstractions;

/// <summary>Speech-to-text over a completed recording.</summary>
/// <remarks>
/// Batch rather than streaming, which is a real constraint worth stating plainly: Parakeet
/// through sherpa-onnx is an <i>offline</i> recogniser, so there is no partial transcript to
/// show while the user is still talking. The HUD shows a level meter during the hold and the
/// text arrives at the end. A streaming engine could implement this same interface by
/// raising <see cref="PartialAvailable"/>; nothing else would change.
/// </remarks>
public interface ITranscriber : IDisposable
{
    /// <summary>Raised with revised in-progress text, if the engine supports it.</summary>
    event Action<string>? PartialAvailable;

    /// <summary>Loads the model. Expensive (seconds); call once at startup, not per utterance.</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>True once <see cref="LoadAsync"/> has succeeded.</summary>
    bool IsLoaded { get; }

    /// <summary>Transcribes one complete recording.</summary>
    /// <param name="samples">16 kHz mono float32 in [-1, 1].</param>
    Task<string> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken ct = default);
}

public sealed class TranscriberException(string message, Exception? inner = null)
    : Exception(message, inner);
