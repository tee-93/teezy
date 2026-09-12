using System.Diagnostics;
using Teezy.Core.Abstractions;
using Teezy.Core.Dictionary;
using Teezy.Core.Formatting;
using Teezy.Core.Hotkeys;

namespace Teezy.Core;

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
/// Turns a spoken utterance into typed text: clean it up, apply the dictionary, inject it.
/// </summary>
/// <remarks>
/// <para>
/// The hold, the microphone and the recogniser are <see cref="VoiceSession"/>'s business, not
/// this class's. What is left here is everything specific to <i>dictation</i> as opposed to any
/// other thing one could do with one's voice — which is the whole reason the split exists.
/// </para>
/// <para>
/// The session stays in <see cref="DictationState.Finishing"/> until this handler returns, so
/// the HUD is still up while the Claude tier is thinking.
/// </para>
/// </remarks>
public sealed class DictationController
{
    private readonly VoiceSession _session;
    private readonly ITextInjector _injector;
    private readonly DictionaryStore _dictionary;
    private readonly Func<TeezySettings> _settings;
    private readonly IForegroundApp _foregroundApp;

    /// <summary>Built per utterance so a settings change takes effect on the very next hold
    /// rather than needing a restart.</summary>
    private readonly Func<ITextFormatter> _formatter;

    public event Action<DictationCompleted>? Completed;

    public DictationState State => _session.State;

    /// <param name="session">
    /// Shared, not owned. Every voice mode registers on the same session because there is one
    /// hook and one microphone; whoever created it disposes it.
    /// </param>
    public DictationController(
        VoiceSession session,
        ITextInjector injector,
        DictionaryStore dictionary,
        Func<TeezySettings> settings,
        IForegroundApp? foregroundApp = null,
        Func<ITextFormatter>? formatter = null)
    {
        _foregroundApp = foregroundApp ?? new UnknownForegroundApp();
        _formatter = formatter ?? (() => new RuleBasedFormatter());
        _injector = injector;
        _dictionary = dictionary;
        _settings = settings;

        _session = session;
        _session.Handle(HotkeyAction.Dictate, OnDictated);
    }

    // ---- The dictation tail ----

    private async Task OnDictated(VoiceResult result)
    {
        // Read before cleanup, not after. Per-app rules need to know where the text is
        // going while there is still a decision to make, and this is also the more
        // truthful moment: it is what had focus when the words were spoken, rather than
        // wherever focus drifted during a second of network round trip.
        var app = _foregroundApp.Current;
        var settings = _settings();

        var stage = Stopwatch.GetTimestamp();
        var formatter = settings.CleanupEnabled ? _formatter() : null;
        var cleaned = formatter is null
            ? result.Text.Trim()
            : await formatter.FormatAsync(result.Text, new FormatContext(app)).ConfigureAwait(false);
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
            text,
            result.Held,
            Stopwatch.GetElapsedTime(result.ReleasedAtTicks),
            injection,
            corrections,
            app,
            usage?.LastTokens,
            usage?.LastModel,
            new StageTimings(result.Transcribe, cleanup, inject)));
    }

}
