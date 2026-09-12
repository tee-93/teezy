using System.Diagnostics;
using Teezy.Core.Abstractions;
using Teezy.Core.Hotkeys;

namespace Teezy.Core;

public enum DictationState
{
    Idle,
    Starting,
    Listening,
    /// <summary>Key released; transcribing, and whatever the handler does with the result.</summary>
    Finishing,
    Error,
}

/// <summary>One utterance, transcribed and handed to whoever asked for it.</summary>
/// <param name="Action">Which binding was held, so a handler knows why it was called.</param>
/// <param name="Text">The raw transcript. Untouched — no cleanup, no dictionary.</param>
/// <param name="Held">How long the key was down.</param>
/// <param name="Transcribe">What the recogniser cost, for the stage breakdown in Insights.</param>
/// <param name="ReleasedAtTicks">
/// A <see cref="Stopwatch"/> timestamp taken the moment the key came up, so a handler can
/// report the whole wait the user actually experienced rather than only its own part of it.
/// </param>
public sealed record VoiceResult(
    HotkeyAction Action,
    string Text,
    TimeSpan Held,
    TimeSpan Transcribe,
    long ReleasedAtTicks);

/// <summary>
/// Hold a key, capture audio, release, transcribe — the part every voice mode shares.
/// </summary>
/// <remarks>
/// <para>
/// Everything up to "here are the words" lives here; what happens to the words does not. That
/// split is what lets a second mode exist at all: dictation types the text, an assistant would
/// act on it, and neither needs to know about the microphone, the hold, the minimum-hold guard
/// or the state machine.
/// </para>
/// <para>
/// <b>The handler is awaited, and the session stays in <see cref="DictationState.Finishing"/>
/// until it returns.</b> That is deliberate: cleanup through the Claude tier can take seconds,
/// and a session that called itself idle the moment the recogniser finished would drop the HUD
/// while the user was still waiting for their text.
/// </para>
/// <para>
/// Platform-neutral by construction — every OS-specific capability arrives as an interface,
/// which is what lets this be tested without a microphone or a keyboard hook.
/// </para>
/// </remarks>
public sealed class VoiceSession : IDisposable
{
    private readonly IHotkeySource _hotkey;
    private readonly IAudioCapture _capture;
    private readonly ITranscriber _transcriber;
    private readonly Func<TeezySettings> _settings;

    private readonly Dictionary<HotkeyAction, Func<VoiceResult, Task>> _handlers = [];

    /// <summary>Serialises state transitions. Press and release arrive on the hook thread,
    /// audio on the capture thread, and the tail runs on a pool thread.</summary>
    private readonly Lock _gate = new();

    private readonly List<float> _buffer = new(AudioChunk.SampleRate * 30);

    private DictationState _state = DictationState.Idle;
    private long _holdStartedTicks;
    private HotkeyAction _active;

    /// <summary>
    /// State, and which mode it belongs to.
    /// </summary>
    /// <remarks>
    /// The action is part of the signal rather than something to look up afterwards: the UI
    /// has a different window per mode, and reading "which mode is active" separately from
    /// "what it is doing" is a race waiting to show the wrong pill.
    /// </remarks>
    public event Action<HotkeyAction, DictationState>? StateChanged;

    public event Action<float>? LevelChanged;
    public event Action<HotkeyAction, string>? Failed;

    public DictationState State
    {
        get { lock (_gate) return _state; }
    }

    public VoiceSession(
        IHotkeySource hotkey,
        IAudioCapture capture,
        ITranscriber transcriber,
        Func<TeezySettings> settings)
    {
        _hotkey = hotkey;
        _capture = capture;
        _transcriber = transcriber;
        _settings = settings;

        _hotkey.Pressed += OnPressed;
        _hotkey.Released += OnReleased;
        _capture.ChunkAvailable += OnChunk;
        _capture.LevelChanged += level => LevelChanged?.Invoke(level);
    }

    /// <summary>Registers what to do with an utterance produced by one binding.</summary>
    /// <remarks>
    /// Only actions registered here are bound to a key, so an unhandled action is inert rather
    /// than a hotkey that opens the microphone and throws the result away.
    /// </remarks>
    public void Handle(HotkeyAction action, Func<VoiceResult, Task> handler) =>
        _handlers[action] = handler;

    public bool Start()
    {
        _hotkey.Bindings = _settings().Bindings()
            .Where(b => _handlers.ContainsKey(b.Key))
            .ToDictionary(b => b.Key, b => b.Value);

        return _hotkey.Start();
    }

    public void Stop() => _hotkey.Stop();

    /// <summary>Re-arms the hook after the user picks different keys.</summary>
    public bool ReloadHotkeys()
    {
        _hotkey.Stop();
        return Start();
    }

    // ---- Hotkey ----

    private void OnPressed(HotkeyAction action)
    {
        if (!_handlers.ContainsKey(action)) return;

        lock (_gate)
        {
            // Only Idle may start. Notably this rejects a press during Finishing, which is
            // the window that matters: transcription plus whatever the handler does can run
            // for a few hundred milliseconds, and without this guard a quick second press
            // would re-enter the tail and read the same buffer twice.
            if (_state != DictationState.Idle) return;
            _buffer.Clear();
            _active = action;
            _holdStartedTicks = Stopwatch.GetTimestamp();
            SetState(DictationState.Starting);
        }

        try
        {
            // Read per utterance, like the formatter and for the same reason: choosing a
            // different microphone in Settings should apply on the very next hold rather
            // than at the next restart. Applied here rather than on change so it can never
            // swap devices during a recording that is already running.
            _capture.PreferredDeviceId = _settings().InputDeviceId;

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

    private void OnReleased(HotkeyAction action)
    {
        TimeSpan held;
        HotkeyAction which;

        lock (_gate)
        {
            if (_state is not (DictationState.Starting or DictationState.Listening)) return;

            // A different binding letting go is not this utterance ending. It happens for
            // real: adding a key hands over from one binding to another, and the release of
            // the one we are not recording for must be ignored.
            if (action != _active) return;

            which = _active;
            held = Stopwatch.GetElapsedTime(_holdStartedTicks);
            SetState(DictationState.Finishing);
        }

        _capture.Stop();
        LevelChanged?.Invoke(0);

        // Fire-and-forget is deliberate: the hook callback must return promptly or Windows
        // silently evicts the hook. All completion is reported through events.
        _ = Task.Run(() => FinishAsync(which, held));
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

    private async Task FinishAsync(HotkeyAction action, TimeSpan held)
    {
        var released = Stopwatch.GetTimestamp();
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

            var stage = Stopwatch.GetTimestamp();
            var raw = await _transcriber.TranscribeAsync(samples).ConfigureAwait(false);
            var transcribe = Stopwatch.GetElapsedTime(stage);

            if (string.IsNullOrWhiteSpace(raw))
            {
                Reset();
                return;
            }

            if (_handlers.TryGetValue(action, out var handler))
            {
                await handler(new VoiceResult(action, raw, held, transcribe, released))
                    .ConfigureAwait(false);
            }

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
        StateChanged?.Invoke(_active, next);
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

        Failed?.Invoke(_active, message);
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
