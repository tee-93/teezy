using Teezy.Core.Commands;
using Teezy.Core.Hotkeys;

namespace Teezy.Core;

/// <summary>How an utterance was dealt with, and therefore how to show it.</summary>
public enum AssistantResult
{
    /// <summary>A command ran. <see cref="AssistantOutcome.Message"/> says what it did.</summary>
    Did,

    /// <summary>A question was answered. The message is prose to read.</summary>
    Answered,

    /// <summary>Nothing matched. The transcript is the useful part.</summary>
    NotUnderstood,

    /// <summary>Understood, but it could not be carried out.</summary>
    Failed,
}

/// <summary>What happened to one spoken command.</summary>
public sealed record AssistantOutcome(
    string Heard,
    VoiceCommand? Command,
    string Message,
    AssistantResult Result);

/// <summary>
/// Turns a spoken utterance into an action on this machine, or an answer.
/// </summary>
/// <remarks>
/// <para>
/// The assistant half of <see cref="VoiceSession"/>, sitting where
/// <see cref="DictationController"/> sits for dictation.
/// </para>
/// <para>
/// <b>Local patterns first, always.</b> <see cref="CommandMatcher"/> handles the everyday
/// vocabulary instantly, offline and free; only what it declines costs a network round trip,
/// and only when the user has switched that on. Someone with the smarter tier off gets exactly
/// the behaviour they had before, including the honest "I can't do that yet".
/// </para>
/// </remarks>
public sealed class AssistantController
{
    private readonly ICommandRunner? _runner;
    private readonly IAssistantFallback? _fallback;

    /// <summary>Raised when a command has been dealt with, however it turned out.</summary>
    public event Action<AssistantOutcome>? Finished;

    /// <summary>Raised when the local patterns declined and the smarter tier is being asked.</summary>
    /// <remarks>
    /// Exists so the pill can say "Thinking" for the second or so that costs. Local matching is
    /// instant and needs no such warning, which is why this is not simply part of Finished.
    /// </remarks>
    public event Action? Thinking;

    /// <param name="runner">Null means understand but do nothing.</param>
    /// <param name="fallback">Null, or switched off, means the local vocabulary is all there is.</param>
    public AssistantController(
        VoiceSession session,
        ICommandRunner? runner = null,
        IAssistantFallback? fallback = null)
    {
        _runner = runner;
        _fallback = fallback;
        session.Handle(HotkeyAction.Assistant, OnSpoken);
    }

    private async Task OnSpoken(VoiceResult result)
    {
        var heard = result.Text.Trim();

        if (CommandMatcher.Match(heard) is { } local)
        {
            await Perform(heard, local).ConfigureAwait(false);
            return;
        }

        if (_fallback is not { IsAvailable: true })
        {
            Finished?.Invoke(new AssistantOutcome(heard, null, "I can’t do that yet", AssistantResult.NotUnderstood));
            return;
        }

        Thinking?.Invoke();

        AssistantReply reply;
        try
        {
            reply = await _fallback.AskAsync(heard).ConfigureAwait(false);
        }
        catch (AssistantUnavailableException e)
        {
            // Not the same as "I don't know", and said differently: someone whose wifi is down
            // should not go away rephrasing themselves.
            Finished?.Invoke(new AssistantOutcome(heard, null, e.Message, AssistantResult.Failed));
            return;
        }

        if (reply.Command is { } chosen)
        {
            await Perform(heard, chosen).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(reply.Answer))
        {
            Finished?.Invoke(new AssistantOutcome(heard, null, reply.Answer.Trim(), AssistantResult.Answered));
            return;
        }

        Finished?.Invoke(new AssistantOutcome(heard, null, "I can’t do that yet", AssistantResult.NotUnderstood));
    }

    private async Task Perform(string heard, VoiceCommand command)
    {
        if (_runner is null)
        {
            Finished?.Invoke(new AssistantOutcome(heard, command, Describe(command), AssistantResult.Did));
            return;
        }

        try
        {
            var message = await _runner.RunAsync(command).ConfigureAwait(false);
            Finished?.Invoke(new AssistantOutcome(heard, command, message, AssistantResult.Did));
        }
        catch (CommandFailedException e)
        {
            // Understood, but could not be done. A different thing to say than "I can't do
            // that", and the transcript goes along too so the user can see it was heard right.
            Finished?.Invoke(new AssistantOutcome(heard, command, e.Message, AssistantResult.Failed));
        }
    }

    /// <summary>What a command would do, for when there is nothing wired up to do it.</summary>
    internal static string Describe(VoiceCommand command) => command switch
    {
        VoiceCommand.LaunchApp app => $"Would open {app.Query}",
        VoiceCommand.SetVolume v => $"Would set volume to {v.Percent}%",
        VoiceCommand.AdjustVolume v => v.Delta > 0 ? "Would turn it up" : "Would turn it down",
        VoiceCommand.Mute { On: true } => "Would mute",
        VoiceCommand.Mute => "Would unmute",
        VoiceCommand.Media { Key: MediaKey.Next } => "Would skip forward",
        VoiceCommand.Media { Key: MediaKey.Previous } => "Would skip back",
        VoiceCommand.Media => "Would play or pause",
        VoiceCommand.LockScreen => "Would lock the PC",
        _ => "Understood",
    };
}
