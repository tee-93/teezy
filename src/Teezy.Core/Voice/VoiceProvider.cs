namespace Teezy.Core;

/// <summary>Which tier reads assistant answers out loud.</summary>
public enum VoiceProvider
{
    /// <summary>The voices already on the machine. Free, offline, instant, unlovely.</summary>
    Windows,

    /// <summary>A paid cloud voice. Better to listen to, slower to start, metered by character.</summary>
    ElevenLabs,

    /// <summary>
    /// Kokoro, a neural voice run on this computer. Free and offline like Windows, and close to
    /// the paid tier to listen to; costs a one-off 170 MB download and a second before it starts.
    /// </summary>
    Kokoro,
}
