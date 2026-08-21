namespace Wisper.Core.Abstractions;

/// <summary>Turns raw amplitude into something a meter can display.</summary>
/// <remarks>
/// Lives in the neutral project rather than next to the capture code because it is pure
/// arithmetic with a wrong answer that is easy to ship and hard to notice — which is
/// exactly what happened: a linear mapping left the meter as a row of motionless dots.
/// Here it is covered by tests.
/// </remarks>
public static class AudioLevel
{
    /// <summary>Quiet room. Below this the meter reads empty.</summary>
    public const double FloorDb = -55.0;

    /// <summary>Talking clearly at a normal distance. At and above this the meter is full.</summary>
    public const double CeilingDb = -12.0;

    /// <summary>Maps RMS amplitude in [0, 1] to a meter reading in [0, 1].</summary>
    /// <remarks>
    /// <para>
    /// Logarithmic, because loudness perception is. Speech into a laptop array microphone
    /// lands around 0.02–0.08 RMS, which a linear mapping renders as almost nothing — the
    /// meter never leaves the bottom few pixels and reads as "not hearing you".
    /// </para>
    /// <para>
    /// The input is floored before the logarithm: <c>log10(0)</c> is negative infinity, and
    /// digital silence is exactly zero whenever microphone access is blocked.
    /// </para>
    /// </remarks>
    public static float ToMeter(double rms)
    {
        var db = 20.0 * Math.Log10(Math.Max(rms, 1e-7));
        return (float)Math.Clamp((db - FloorDb) / (CeilingDb - FloorDb), 0.0, 1.0);
    }
}
