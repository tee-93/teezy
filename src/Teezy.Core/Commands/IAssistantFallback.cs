namespace Teezy.Core.Commands;

/// <summary>
/// What a smarter tier made of an utterance the patterns could not place.
/// </summary>
/// <param name="Command">
/// One of the closed set, chosen — never invented. Null when the request was not an action.
/// </param>
/// <param name="Answer">A short reply to show, when the request was a question.</param>
/// <remarks>
/// Exactly one of the two is expected. Both null means it had nothing useful to offer, which
/// is a perfectly good outcome and reported as such.
/// </remarks>
public sealed record AssistantReply(VoiceCommand? Command = null, string? Answer = null)
{
    public static AssistantReply Nothing => new();

    public bool IsNothing => Command is null && string.IsNullOrWhiteSpace(Answer);
}

/// <summary>
/// A smarter interpreter for what the local patterns did not recognise.
/// </summary>
/// <remarks>
/// <para>
/// Reached only after <see cref="CommandMatcher"/> has declined, so the common vocabulary stays
/// instant, offline and free, and only the long tail costs a network round trip.
/// </para>
/// <para>
/// <b>The implementation chooses from <see cref="VoiceCommand"/>; it never names an action of
/// its own.</b> That is the contract, and it is why this returns a typed command rather than a
/// string to execute: the worst a confused model can do is pick the wrong item off a list that
/// contains nothing destructive.
/// </para>
/// <para>
/// <b>It is given the user's words and nothing else.</b> No screen, no clipboard, no documents.
/// With no untrusted text anywhere near it there is no prompt injection surface at all, and
/// that property is worth defending every time this interface grows.
/// </para>
/// </remarks>
public interface IAssistantFallback
{
    /// <summary>Whether it is switched on and able to answer. Checked before asking.</summary>
    bool IsAvailable { get; }

    /// <summary>Interprets an utterance the patterns did not match.</summary>
    /// <exception cref="AssistantUnavailableException">The tier could not be reached.</exception>
    Task<AssistantReply> AskAsync(string spoken, CancellationToken ct = default);
}

/// <summary>The smarter tier was switched on but could not be reached.</summary>
/// <remarks>
/// Distinct from a reply of nothing. "No network" and "I don't know" want different words, and
/// collapsing them would have someone debugging their phrasing when the problem is their wifi.
/// </remarks>
public sealed class AssistantUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
