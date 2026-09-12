namespace Teezy.Core.Commands;

/// <summary>Actually performs a command, and says what it did.</summary>
/// <remarks>
/// The boundary between "we understood you" and "we touched the machine". Everything on this
/// side of it is platform-specific and untestable without a desktop, which is exactly why
/// matching lives in <see cref="CommandMatcher"/> and this does not.
/// </remarks>
public interface ICommandRunner
{
    /// <summary>Performs the command.</summary>
    /// <returns>
    /// A short line to show the user, phrased as what happened rather than what was asked —
    /// "Volume 40%" rather than "Setting volume", because by the time it is read it is done.
    /// </returns>
    /// <exception cref="CommandFailedException">The command could not be carried out.</exception>
    Task<string> RunAsync(VoiceCommand command, CancellationToken ct = default);
}

/// <summary>A command that was understood but could not be carried out.</summary>
/// <remarks>
/// Distinct from not understanding, and shown differently: "I couldn't find Chrome" tells the
/// user something entirely different from "I can't do that yet".
/// </remarks>
public sealed class CommandFailedException(string message, Exception? inner = null)
    : Exception(message, inner);
