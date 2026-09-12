namespace Teezy.Core;

/// <summary>Which tier reads assistant answers out loud.</summary>
public enum VoiceProvider
{
    /// <summary>The voices already on the machine. Free, offline, instant, unlovely.</summary>
    Windows,

    /// <summary>A paid cloud voice. Better to listen to, slower to start, metered by character.</summary>
    ElevenLabs,
}
