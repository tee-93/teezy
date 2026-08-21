namespace Teezy.Core.Abstractions;

/// <summary>Whether the app launches itself when the user signs in.</summary>
/// <remarks>
/// <para>
/// <b>The operating system is the single source of truth, not a saved setting.</b> Windows
/// lets the user turn a startup entry off in Task Manager, and macOS has its own Login Items
/// pane. Mirroring the state into <c>settings.json</c> would let the two disagree, and the
/// app would confidently show a tick next to something that no longer happens.
/// </para>
/// <para>
/// So there is deliberately no <c>Autostart</c> property on <see cref="TeezySettings"/>:
/// every read goes to the OS.
/// </para>
/// </remarks>
public interface IAutostart
{
    /// <summary>True only if the app is registered <i>and</i> the user has not disabled it.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// True when the app is registered but the user switched it off outside the app —
    /// Task Manager's Startup tab on Windows.
    /// </summary>
    /// <remarks>
    /// Worth surfacing rather than silently re-enabling. Someone who turned it off in Task
    /// Manager meant it, and a checkbox that quietly overrode them would be a bad citizen.
    /// </remarks>
    bool IsBlockedByUser { get; }

    void Enable();

    void Disable();
}

/// <summary>Used where no platform implementation exists; autostart is simply unavailable.</summary>
public sealed class UnsupportedAutostart : IAutostart
{
    public bool IsEnabled => false;
    public bool IsBlockedByUser => false;
    public void Enable() { }
    public void Disable() { }
}
