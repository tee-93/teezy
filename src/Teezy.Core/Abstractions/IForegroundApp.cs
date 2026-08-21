namespace Teezy.Core.Abstractions;

/// <summary>Identifies the app that dictated text is about to land in.</summary>
/// <remarks>
/// Only used to label history entries and build the usage breakdown. It reports a process
/// name, never a window title: titles routinely contain document names, email subjects and
/// URLs, and none of that belongs in a stats file.
/// </remarks>
public interface IForegroundApp
{
    /// <summary>Process name of the focused window, or null if it cannot be determined.</summary>
    string? Current { get; }
}

/// <summary>Used when no platform implementation is supplied; history simply goes unlabelled.</summary>
public sealed class UnknownForegroundApp : IForegroundApp
{
    public string? Current => null;
}
