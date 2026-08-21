using System.Diagnostics;
using Teezy.Core.Abstractions;
using Teezy.Core.Dictionary;
using Teezy.Core.Formatting;

namespace Teezy.Core;

public enum DictationState
{
    Idle,
    Starting,
    Listening,
    /// <summary>Key released; transcribing, cleaning up and injecting.</summary>
    Finishing,
    Error,
}

public sealed record DictationCompleted(
    string Text,
    TimeSpan AudioDuration,
    TimeSpan ProcessingTime,
    InjectionResult Injection,
    IReadOnlyList<AppliedCorrection> Corrections,
    string? App);

/// <summary>
/// Owns the push-to-talk lifecycle: hold the key, capture audio, release, transcribe, clean
/// up, inject.
/// </summary>
/// <remarks>
/// Platform-neutral by construction — every OS-specific capability arrives as an interface,
/// which is what lets this logic be tested without a microphone, a keyboard hook, or a
/// foreground window.
/// </remarks>
public sealed class DictationController : IDisposable
{
    private readonly IHotkeySource _hotkey;
    private readonly IAudioCapture _capture;
    private readonly ITranscriber _transcriber;
    private readonly ITextInjector _injector;
    private readonly DictionaryStore _dictionary;
    private readonly Func<TeezySettings> _settings;
    private readonly IForegroundApp _foregroundApp;

    /// <summary>Serialises state transitions. Press and release arrive on the hook thread,
    /// audio on the capture thread, and the tail runs on a pool thread.</summary>
    private readonly Lock _gate = new();

    private readonly List<float> _buffer = new(AudioChunk.SampleRate * 30);
    private DictationState _state = DictationState.Idle;
    private long _holdStartedTicks;

    public event Action<DictationState>? StateChanged;
    public event Action<float>? LevelChanged;
    public event Action<DictationCompleted>? Completed;
    public event Action<string>? Failed;

    public DictationState State
    {
        get { lock (_gate) return _state; }
    }

    public DictationController(
        IHotkeySource hotkey,
        IAudioCapture capture,
        ITranscriber transcriber,
        ITextInjector injector,
        DictionaryStore dictionary,
        Func<TeezySettings> settings,
        IForegroundApp? foregroundApp = null)
    {
        _foregroundApp = foregroundApp ?? new UnknownForegroundApp();
        _hotkey = hotkey;
        _capture = capture;
        _transcriber = transcriber;
        _injector = injector;
        _dictionary = dictionary;
        _settings = settings;

        _hotkey.Pressed += OnPressed;
        _hotkey.Released += OnReleased;
        _capture.ChunkAvailable += OnChunk;
        _capture.LevelChanged += level => LevelChanged?.Invoke(level);
    }

    public bool Start()
    {
        _hotkey.Key = _settings().PushToTalkKey;
        return _hotkey.Start();
    }

    public void Stop() => _hotkey.Stop();

    /// <summary>Re-arms the hook after the user picks a different key.</summary>
    public bool ReloadHotkey()
    {
        _hotkey.Stop();
        return Start();
    }

    // ---- Hotkey ----

    private void OnPressed()
    {
        lock (_gate)
        {
            // Only Idle may start. Notably this rejects a press during Finishing, which is
            // the window that matters: transcription plus cleanup can run for a few hundred
            // milliseconds, and without this guard a quick second press would re-enter the
            // tail, read the same buffer, and type the utterance twice.
            if (_state != DictationState.Idle) return;
            _buffer.Clear();
            _holdStartedTicks = Stopwatch.GetTimestamp();
            SetState(DictationState.Starting);
        }

        try
        {
            _capture.Start();
            lock (_gate)
            {
                // The user may have already let go while the device was opening.
                if (_state != DictationState.Starting) return;
                SetState(DictationState.Listening);
            }
        }
        catch (AudioCaptureException e)
        {
            Fail(e.Message);
        }
    }

    private void OnReleased()
    {
        TimeSpan held;
        lock (_gate)
        {
            if (_state is not (DictationState.Starting or DictationState.Listening)) return;
            held = Stopwatch.GetElapsedTime(_holdStartedTicks);
            SetState(DictationState.Finishing);
        }

        _capture.Stop();
        LevelChanged?.Invoke(0);

        // Fire-and-forget is deliberate: the hook callback must return promptly or Windows
        // silently evicts the hook. All completion is reported through events.
        _ = Task.Run(() => FinishAsync(held));
    }

    private void OnChunk(AudioChunk chunk)
    {
        lock (_gate)
        {
            if (_state is not (DictationState.Listening or DictationState.Starting)) return;
            _buffer.AddRange(chunk.Samples);
        }
    }

    // ---- The tail ----

    private async Task FinishAsync(TimeSpan held)
    {
        var started = Stopwatch.GetTimestamp();
        var settings = _settings();

        try
        {
            float[] samples;
            lock (_gate) samples = [.. _buffer];

            if (held.TotalMilliseconds < settings.MinimumHoldMilliseconds || samples.Length == 0)
            {
                Reset();
                return;
            }

            var raw = await _transcriber.TranscribeAsync(samples).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(raw))
            {
                Reset();
                return;
            }

            var cleaned = settings.CleanupEnabled
                ? await new RuleBasedFormatter().FormatAsync(raw).ConfigureAwait(false)
                : raw.Trim();

            // The dictionary runs last and runs unconditionally. Biasing only improves the
            // odds; this is the pass that guarantees the spelling, so it must not be
            // something the user can switch off by accident along with cleanup.
            var (text, corrections) = _dictionary.Corrector.Apply(cleaned);

            // Read the target before injecting. Afterwards is too late in the cases that
            // matter: a paste can shift focus, and an app that closes on submit would be gone.
            var app = _foregroundApp.Current;
            var injection = _injector.Insert(text);

            Completed?.Invoke(new DictationCompleted(
                text, held, Stopwatch.GetElapsedTime(started), injection, corrections, app));

            Reset();
        }
        catch (Exception e) when (e is TranscriberException or InvalidOperationException)
        {
            Fail(e.Message);
        }
    }

    // ---- State ----

    /// <summary>Must be called with <see cref="_gate"/> held.</summary>
    private void SetState(DictationState next)
    {
        _state = next;
        StateChanged?.Invoke(next);
    }

    private void Reset()
    {
        lock (_gate)
        {
            _buffer.Clear();
            SetState(DictationState.Idle);
        }
    }

    private void Fail(string message)
    {
        try { _capture.Stop(); } catch (AudioCaptureException) { /* already down */ }

        lock (_gate)
        {
            _buffer.Clear();
            SetState(DictationState.Error);
        }

        Failed?.Invoke(message);
        LevelChanged?.Invoke(0);

        // Drop back to Idle so one bad utterance doesn't strand the app.
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
        {
            lock (_gate)
            {
                if (_state == DictationState.Error) SetState(DictationState.Idle);
            }
        }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _hotkey.Stop();
        _hotkey.Dispose();
        _capture.Dispose();
        _transcriber.Dispose();
    }
}
