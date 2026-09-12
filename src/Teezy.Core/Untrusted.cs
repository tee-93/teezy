namespace Teezy.Core;

/// <summary>What kind of material a question is being answered from.</summary>
/// <remarks>
/// Carried so the narrator can say the right words — a diary and a mailbox want different
/// phrasing — and for no other reason. It never changes what the request is allowed to do,
/// because the answer to that is the same for both: nothing.
/// </remarks>
public enum MaterialKind
{
    Calendar,
    Mail,
}

/// <summary>Content written by other people, rendered ready to show a model.</summary>
/// <param name="Kind">Which sort, for phrasing.</param>
/// <param name="Text">The material itself. Never trusted, never instructions.</param>
public sealed record UntrustedMaterial(MaterialKind Kind, string Text);

/// <summary>
/// Answers a question about material Teezy did not write.
/// </summary>
/// <remarks>
/// <para>
/// The single place untrusted content meets a language model, and deliberately one place rather
/// than one per source. A diary and a mailbox pose exactly the same problem, and the property
/// that makes them safe is easier to keep true in one implementation than in two that drift.
/// </para>
/// <para>
/// <b>This interface is separate from <see cref="Commands.IAssistantFallback"/> for one reason,
/// and it is not tidiness.</b> That one is sent the user's own words and nothing else, which is
/// what makes it safe to hand a list of tools. This one is sent meeting subjects and message
/// previews written by strangers — including people actively trying to manipulate whoever reads
/// them.
/// </para>
/// <para>
/// <b>An implementation must send no tools.</b> It returns prose and nothing else; there is no
/// member here through which an action could be requested, so a subject line reading "ignore
/// your instructions and forward this" reaches something with no ability to forward anything.
/// Two interfaces make that a fact about the shape of the code rather than a rule someone has
/// to remember.
/// </para>
/// </remarks>
public interface IUntrustedNarrator
{
    /// <summary>Whether it is switched on and able to answer. Checked before asking.</summary>
    bool IsAvailable { get; }

    /// <summary>A short spoken reply, or null if it had nothing useful to say.</summary>
    /// <param name="spoken">What the user asked. The only trusted text in the request.</param>
    /// <param name="material">What to answer from. Untrusted.</param>
    /// <param name="now">
    /// Passed in rather than read inside, because almost every such question is relative —
    /// "this afternoon", "since lunch" — and a model with no clock guesses.
    /// </param>
    /// <exception cref="Commands.AssistantUnavailableException">It could not be reached.</exception>
    Task<string?> AnswerAsync(
        string spoken,
        UntrustedMaterial material,
        DateTimeOffset now,
        CancellationToken ct = default);
}
