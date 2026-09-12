using Teezy.Core.Calendar;
using Teezy.Core.Mail;
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
    private readonly CombinedCalendar? _calendar;
    private readonly CombinedMailbox? _mailbox;
    private readonly IUntrustedNarrator? _narrator;
    private readonly Func<DateTimeOffset> _now;

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
    /// <param name="calendar">Null, or nothing connected, means diary questions are not claimed.</param>
    /// <param name="narrator">Null means only the everyday diary phrasings can be answered.</param>
    /// <param name="now">Overridable so the phrasing can be tested at a fixed hour.</param>
    public AssistantController(
        VoiceSession session,
        ICommandRunner? runner = null,
        IAssistantFallback? fallback = null,
        CombinedCalendar? calendar = null,
        CombinedMailbox? mailbox = null,
        IUntrustedNarrator? narrator = null,
        Func<DateTimeOffset>? now = null)
    {
        _runner = runner;
        _fallback = fallback;
        _calendar = calendar;
        _mailbox = mailbox;
        _narrator = narrator;
        _now = now ?? (() => DateTimeOffset.Now);
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

        // Ahead of the general fallback, because a connected diary is the better answer to
        // "what's on today" than a model guessing, and because the diary path deliberately
        // offers no tools. Only claimed when something is actually connected — otherwise the
        // question goes to the fallback, which at least says it cannot see a calendar.
        if (_calendar is { IsConnected: true }
            && CalendarQuestion.Classify(heard) is var ask and not CalendarAsk.None)
        {
            await AnswerFromDiary(heard, ask).ConfigureAwait(false);
            return;
        }

        // The two gates are disjoint in practice — "what's on today" names no mailbox and "any
        // new email" names no diary — so the order between them settles nothing and is simply
        // the order they were built in.
        if (_mailbox is { IsConnected: true }
            && MailQuestion.Classify(heard) is var post and not MailAsk.None)
        {
            await AnswerFromMail(heard, post).ConfigureAwait(false);
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

    /// <summary>Answers a diary question from the diary.</summary>
    /// <remarks>
    /// <b>No tools are reachable from anywhere in here.</b> The everyday phrasings are composed
    /// by <see cref="CalendarAnswer"/> without asking anyone; the rest go to
    /// <see cref="IUntrustedNarrator"/>, which can return prose and nothing else. That is the
    /// structural answer to meeting subjects being written by strangers — see
    /// <see cref="ICalendar"/>.
    /// </remarks>
    private async Task AnswerFromDiary(string heard, CalendarAsk ask)
    {
        var now = _now();
        var (from, to) = CalendarAnswer.Window(ask, now);

        // Reading a calendar is a network call, so the pill should say something while it
        // happens — the same second of silence the smarter tier warns about.
        Thinking?.Invoke();

        CalendarReading reading;
        try
        {
            reading = await _calendar!.BetweenAsync(from, to).ConfigureAwait(false);
        }
        catch (CalendarUnavailableException e)
        {
            Finished?.Invoke(new AssistantOutcome(heard, null, e.Message, AssistantResult.Failed));
            return;
        }

        if (CalendarAnswer.For(ask, reading, now) is { } composed)
        {
            Finished?.Invoke(new AssistantOutcome(heard, null, composed, AssistantResult.Answered));
            return;
        }

        if (_narrator is not { IsAvailable: true })
        {
            // Honest about the edge of what it can do, rather than answering a different
            // question with the day's list and letting the user work out it was not asked.
            Finished?.Invoke(new AssistantOutcome(
                heard,
                null,
                "I can tell you what’s next, what’s on today, or tomorrow.",
                AssistantResult.NotUnderstood));
            return;
        }

        string? answer;
        try
        {
            answer = await _narrator
                .AnswerAsync(heard, CalendarAnswer.Material(reading.Events, now), now)
                .ConfigureAwait(false);
        }
        catch (AssistantUnavailableException e)
        {
            Finished?.Invoke(new AssistantOutcome(heard, null, e.Message, AssistantResult.Failed));
            return;
        }

        Finished?.Invoke(string.IsNullOrWhiteSpace(answer)
            ? new AssistantOutcome(heard, null, "I can’t do that yet", AssistantResult.NotUnderstood)
            : new AssistantOutcome(heard, null, answer.Trim(), AssistantResult.Answered));
    }

    /// <summary>Answers a mail question from the mailbox.</summary>
    /// <remarks>
    /// The same two tiers and the same rule as the diary, and the rule matters more here — see
    /// <see cref="IMailbox"/>. The everyday phrasings never leave the machine; the rest go to
    /// <see cref="IUntrustedNarrator"/>, which carries no tools.
    /// </remarks>
    private async Task AnswerFromMail(string heard, MailAsk ask)
    {
        var now = _now();
        var (since, atMost) = MailAnswer.Window(ask, now);

        Thinking?.Invoke();

        MailReading reading;
        try
        {
            reading = await _mailbox!.RecentAsync(since, atMost).ConfigureAwait(false);
        }
        catch (MailUnavailableException e)
        {
            Finished?.Invoke(new AssistantOutcome(heard, null, e.Message, AssistantResult.Failed));
            return;
        }

        if (MailAnswer.For(ask, reading, now) is { } composed)
        {
            Finished?.Invoke(new AssistantOutcome(heard, null, composed, AssistantResult.Answered));
            return;
        }

        if (_narrator is not { IsAvailable: true })
        {
            Finished?.Invoke(new AssistantOutcome(
                heard,
                null,
                "I can tell you what’s unread, or what came in today.",
                AssistantResult.NotUnderstood));
            return;
        }

        string? answer;
        try
        {
            answer = await _narrator
                .AnswerAsync(heard, MailAnswer.Material(reading.Messages, now), now)
                .ConfigureAwait(false);
        }
        catch (AssistantUnavailableException e)
        {
            Finished?.Invoke(new AssistantOutcome(heard, null, e.Message, AssistantResult.Failed));
            return;
        }

        Finished?.Invoke(string.IsNullOrWhiteSpace(answer)
            ? new AssistantOutcome(heard, null, "I can’t do that yet", AssistantResult.NotUnderstood)
            : new AssistantOutcome(heard, null, answer.Trim(), AssistantResult.Answered));
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
