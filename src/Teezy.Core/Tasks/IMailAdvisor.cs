namespace Teezy.Core.Tasks;

/// <summary>What the advisor is asked for.</summary>
public enum AdviceKind
{
    /// <summary>Two or three concrete next steps for the person the email was sent to.</summary>
    NextSteps,

    /// <summary>A reply they could send, written as them.</summary>
    DraftReply,
}

/// <summary>
/// Suggests next steps for an email pasted into a task, or drafts a reply — only ever when asked,
/// one email at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>An implementation must send no tools</b>, for the same reason <see cref="IUntrustedNarrator"/>
/// must not: an email is written by someone else, often to manipulate whoever reads it. The
/// result is text shown to the user to copy and nothing more — it is not sent anywhere, and there
/// is no member here through which an action could be requested.
/// </para>
/// <para>
/// A separate interface rather than a mode of the narrator because the output differs in kind
/// — a page to read and copy, not a sentence to speak — and keeping them apart keeps each
/// prompt honest about what it is for.
/// </para>
/// </remarks>
public interface IMailAdvisor
{
    /// <summary>Whether it is switched on and able to answer.</summary>
    bool IsAvailable { get; }

    /// <param name="kind">Next steps, or a reply.</param>
    /// <param name="task">The task the email was pasted into, for context: the user's own words.</param>
    /// <param name="email">The pasted email. Untrusted.</param>
    /// <param name="instruction">Anything the user added, e.g. "say yes but not before Friday".</param>
    /// <exception cref="Commands.AssistantUnavailableException">It could not be reached.</exception>
    Task<string?> AdviseAsync(
        AdviceKind kind,
        TaskItem? task,
        string email,
        string? instruction,
        DateTimeOffset now,
        CancellationToken ct = default);
}
