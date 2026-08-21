using Teezy.Core.Abstractions;

namespace Teezy.Core.Tests;

internal sealed class FakeHotkey : IHotkeySource
{
    public event Action? Pressed;
    public event Action? Released;
    public PushToTalkKey Key { get; set; }
    public bool IsStarted { get; private set; }

    public bool Start() { IsStarted = true; return true; }
    public void Stop() => IsStarted = false;
    public void Dispose() { }

    public void Press() => Pressed?.Invoke();
    public void Release() => Released?.Invoke();
}

internal sealed class FakeCapture : IAudioCapture
{
    public event Action<AudioChunk>? ChunkAvailable;
    public event Action<float>? LevelChanged;

    public string? DeviceName => "fake";
    public int StartCount { get; private set; }
    public bool IsRunning { get; private set; }

    /// <summary>Set to make Start throw, standing in for a device that will not open.</summary>
    public bool FailOnStart { get; set; }

    public void Start()
    {
        if (FailOnStart) throw new AudioCaptureException("no device");
        StartCount++;
        IsRunning = true;
    }

    public void Stop() => IsRunning = false;
    public void Dispose() { }

    public void Emit(int samples = 1600) =>
        ChunkAvailable?.Invoke(new AudioChunk(new float[samples]));

    public void EmitLevel(float level) => LevelChanged?.Invoke(level);
}

internal sealed class FakeTranscriber : ITranscriber
{
    public event Action<string>? PartialAvailable;

    public string Result { get; set; } = "hello world";
    public int Calls { get; private set; }
    public int LastSampleCount { get; private set; }

    /// <summary>Held open to simulate a slow transcription, so a second key press can be
    /// delivered while the tail is still running.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public bool IsLoaded => true;
    public Task LoadAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task<string> TranscribeAsync(ReadOnlyMemory<float> samples, CancellationToken ct = default)
    {
        Calls++;
        LastSampleCount = samples.Length;
        if (Gate is not null) await Gate.Task.ConfigureAwait(false);
        return Result;
    }

    public void Dispose() { }
}

internal sealed class FakeInjector : ITextInjector
{
    public List<string> Inserted { get; } = [];

    public InjectionResult Insert(string text)
    {
        lock (Inserted) Inserted.Add(text);
        return InjectionResult.Typed;
    }
}
