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

/// <summary>Where the wait between releasing the key and seeing text actually went.</summary>
/// <remarks>
/// The three stages have completely different causes when they are slow — a slower CPU, a
/// slower network, a slower target application — and completely different fixes. A single
/// total cannot tell them apart, which is exactly the position Teezy was in the first time it
/// ran on a machine that was not the one it was tuned on.
/// </remarks>
public sealed record StageTimings(TimeSpan Transcribe, TimeSpan Cleanup, TimeSpan Inject);

public sealed record DictationCompleted(
    string Text,
    TimeSpan AudioDuration,
    TimeSpan ProcessingTime,
    InjectionResult Injection,
    IReadOnlyList<AppliedCorrection> Corrections,
    string? App,
    Cost.TokenUsage? Tokens = null,
    string? Model = null,
    StageTimings? Stages = null);

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

    /// <summary>Built per utterance so a settings change takes effect on the very next hold
    /// rather than needing a restart.</summary>
    private readonly Func<ITextFormatter> _formatter;

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
        IForegroundApp? foregroundApp = null,
        Func<ITextFormatter>? formatter = null)
    {
        _foregroundApp = foregroundApp ?? new UnknownForegroundApp();
        _formatter = formatter ?? (() => new RuleBasedFormatter());
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
        _hotkey.Hotkey = _settings().Hotkey;
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

            // Each stage is timed separately. One total was enough while the only machine that
            // mattered did the whole thing in 170 ms; the moment Teezy ran somewhere slower,
            // "it is slow" could not be attributed to the model, the network, or the typing,
            // and there was no way to tell from the outside which one to go after.
            var stage = Stopwatch.GetTimestamp();
            var raw = await _transcriber.TranscribeAsync(samples).ConfigureAwait(false);
            var transcribe = Stopwatch.GetElapsedTime(stage);

            if (string.IsNullOrWhiteSpace(raw))
            {
                Reset();
                return;
            }

            // Read before cleanup, not after. Per-app rules need to know where the text is
            // going while there is still a decision to make, and this is also the more
            // truthful moment: it is what had focus when the words were spoken, rather than
            // wherever focus drifted during a second of network round trip.
            var app = _foregroundApp.Current;

            stage = Stopwatch.GetTimestamp();
            var formatter = settings.CleanupEnabled ? _formatter() : null;
            var cleaned = formatter is null
                ? raw.Trim()
                : await formatter.FormatAsync(raw, new FormatContext(app)).ConfigureAwait(false);
            var cleanup = Stopwatch.GetElapsedTime(stage);

            // Read straight after the call, before anything else can run one. Only the paid
            // tier reports this; a local formatter simply is not IReportsUsage and the entry
            // records no tokens, which is the truth rather than a zero.
            var usage = formatter as IReportsUsage;

            // The dictionary runs last and runs unconditionally. Biasing only improves the
            // odds; this is the pass that guarantees the spelling, so it must not be
            // something the user can switch off by accident along with cleanup.
            var (text, corrections) = _dictionary.Corrector.Apply(cleaned);

            stage = Stopwatch.GetTimestamp();
            var injection = _injector.Insert(text);
            var inject = Stopwatch.GetElapsedTime(stage);

            Completed?.Invoke(new DictationCompleted(
                text, held, Stopwatch.GetElapsedTime(started), injection, corrections, app,
                usage?.LastTokens, usage?.LastModel,
                new StageTimings(transcribe, cleanup, inject)));

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
