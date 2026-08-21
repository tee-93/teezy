using System;
using System.IO;
using System.Media;

namespace Wisper.App;

/// <summary>Two short tones: rising when recording opens, falling when it closes.</summary>
/// <remarks>
/// <para>
/// Synthesised rather than shipped as .wav assets, and deliberately not the Windows system
/// sounds — those carry meaning ("something went wrong") that would be actively misleading
/// several times an hour.
/// </para>
/// <para>
/// The envelope matters more than the pitch. A raw sine gated on and off produces an audible
/// click at each discontinuity, so both ends are ramped.
/// </para>
/// </remarks>
internal static class Chime
{
    private const int SampleRate = 44_100;
    private const double DurationSeconds = 0.09;
    private const double Volume = 0.16;

    private static readonly Lazy<SoundPlayer> StartTone = new(() => Build(660, 880));
    private static readonly Lazy<SoundPlayer> StopTone = new(() => Build(880, 590));

    public static void Start() => Play(StartTone);
    public static void Stop() => Play(StopTone);

    private static void Play(Lazy<SoundPlayer> tone)
    {
        try
        {
            tone.Value.Play();   // asynchronous; never blocks the dictation path
        }
        catch (InvalidOperationException)
        {
            // No audio endpoint. A missing chime must never break dictation.
        }
    }

    private static SoundPlayer Build(double fromHz, double toHz)
    {
        var count = (int)(SampleRate * DurationSeconds);
        var stream = new MemoryStream();
        var w = new BinaryWriter(stream);

        // 16-bit mono PCM WAV header.
        var dataBytes = count * 2;
        w.Write("RIFF"u8.ToArray());
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8.ToArray());
        w.Write("fmt "u8.ToArray());
        w.Write(16);
        w.Write((short)1);                      // PCM
        w.Write((short)1);                      // mono
        w.Write(SampleRate);
        w.Write(SampleRate * 2);                // byte rate
        w.Write((short)2);                      // block align
        w.Write((short)16);                     // bits per sample
        w.Write("data"u8.ToArray());
        w.Write(dataBytes);

        var phase = 0.0;
        for (var i = 0; i < count; i++)
        {
            var t = i / (double)count;
            var hz = fromHz + (toHz - fromHz) * t;

            // Accumulate phase rather than computing sin(2*pi*hz*i/rate): with a sweeping
            // frequency the direct form jumps phase every sample and buzzes.
            phase += 2 * Math.PI * hz / SampleRate;

            // Raised-cosine envelope, zero at both ends, so there is no click.
            var envelope = 0.5 * (1 - Math.Cos(2 * Math.PI * Math.Min(t, 1 - t) * 2));
            w.Write((short)(Math.Sin(phase) * envelope * Volume * short.MaxValue));
        }

        w.Flush();
        stream.Position = 0;
        return new SoundPlayer(stream);
    }
}
