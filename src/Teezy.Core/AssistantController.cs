using Teezy.Core.Commands;
using Teezy.Core.Hotkeys;

namespace Teezy.Core;

/// <summary>What happened to one spoken command.</summary>
public sealed record AssistantOutcome(
    /// <summary>What was heard, verbatim. Shown when the news is bad.</summary>
    string Heard,
    /// <summary>The command, or null when nothing matched.</summary>
    VoiceCommand? Command,
    /// <summary>What to tell the user.</summary>
    string Message,
    /// <summary>False when nothing matched, or the command could not be carried out.</summary>
    bool Succeeded);

/// <summary>
/// Turns a spoken utterance into an action on this machine.
/// </summary>
/// <remarks>
/// <para>
/// The assistant half of <see cref="VoiceSession"/>, sitting exactly where
/// <see cref="DictationController"/> sits for dictation: the words arrive, and what happens to
/// them is this class's business.
/// </para>
/// <para>
/// <b>Nothing here interprets freely.</b> <see cref="CommandMatcher"/> either returns one of a
/// closed set of typed commands or returns nothing, and nothing is a perfectly good answer —
/// reported back with the transcript, so the user can tell "you misheard me" from "you can't do
/// that", which need completely different responses from them.
/// </para>
/// </remarks>
public sealed class AssistantController
{
    private readonly ICommandRunner? _runner;

    /// <summary>Raised when a command has been dealt with, successfully or not.</summary>
    public event Action<AssistantOutcome>? Finished;

    /// <param name="runner">
    /// Null means understand but do nothing — useful while the wiring is being built, and the
    /// safest possible default for a feature that presses keys on someone's machine.
    /// </param>
    public AssistantController(VoiceSession session, ICommandRunner? runner = null)
    {
        _runner = runner;
        session.Handle(HotkeyAction.Assistant, OnSpoken);
    }

    private async Task OnSpoken(VoiceResult result)
    {
        var heard = result.Text.Trim();
        var command = CommandMatcher.Match(heard);

        if (command is null)
        {
            Finished?.Invoke(new AssistantOutcome(heard, null, "I can’t do that yet", false));
            return;
        }

        if (_runner is null)
        {
            Finished?.Invoke(new AssistantOutcome(heard, command, Describe(command), true));
            return;
        }

        try
        {
            var message = await _runner.RunAsync(command).ConfigureAwait(false);
            Finished?.Invoke(new AssistantOutcome(heard, command, message, true));
        }
        catch (CommandFailedException e)
        {
            // Understood, but could not be done. A different thing to say than "I can't do
            // that", and the transcript goes along too so the user can see it was heard right.
            Finished?.Invoke(new AssistantOutcome(heard, command, e.Message, false));
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
