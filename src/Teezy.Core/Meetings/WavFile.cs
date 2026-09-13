using System.Buffers.Binary;
using System.Text;
using Teezy.Core.Abstractions;

namespace Teezy.Core.Meetings;

/// <summary>Streams 16 kHz mono audio into a WAV file as it is captured.</summary>
/// <remarks>
/// <para>
/// A meeting goes to disk, not into memory. An hour is 57.6 million samples a side — about
/// 460 MB as floats for both sides — on a laptop already holding a 900 MB speech model and a
/// Teams call. As 16-bit PCM it is 115 MB a side, and none of it sits in RAM.
/// </para>
/// <para>
/// The header's sizes are only known at the end, so they are written as zero and patched on
/// <see cref="Dispose"/>. A recording cut short by a crash keeps the zeros, which
/// <see cref="WavReader"/> reads as "the rest of the file" rather than as empty — so an hour
/// recorded before a crash is an hour that can still be transcribed.
/// </para>
/// </remarks>
public sealed class WavWriter : IDisposable
{
    private const int HeaderBytes = 44;
    private const int BytesPerSample = 2;

    private readonly FileStream _file;
    private byte[] _buffer = new byte[AudioChunk.SampleRate / 10 * BytesPerSample];
    private bool _disposed;

    public WavWriter(string path)
    {
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        WriteHeader(riffSize: 0, dataSize: 0);
    }

    /// <summary>Samples written so far, silence included.</summary>
    public long SamplesWritten { get; private set; }

    public void Write(ReadOnlySpan<float> samples)
    {
        var bytes = samples.Length * BytesPerSample;
        if (_buffer.Length < bytes) _buffer = new byte[bytes];

        for (var i = 0; i < samples.Length; i++)
        {
            var clamped = Math.Clamp(samples[i], -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(
                _buffer.AsSpan(i * BytesPerSample), (short)MathF.Round(clamped * short.MaxValue));
        }

        _file.Write(_buffer, 0, bytes);
        SamplesWritten += samples.Length;
    }

    /// <summary>Writes digital silence, for time a device spent not delivering anything.</summary>
    public void WriteSilence(long samples)
    {
        Array.Clear(_buffer);
        var perBlock = _buffer.Length / BytesPerSample;

        while (samples > 0)
        {
            var n = (int)Math.Min(samples, perBlock);
            _file.Write(_buffer, 0, n * BytesPerSample);
            samples -= n;
            SamplesWritten += n;
        }
    }

    private void WriteHeader(uint riffSize, uint dataSize)
    {
        Span<byte> h = stackalloc byte[HeaderBytes];
        Encoding.ASCII.GetBytes("RIFF", h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], riffSize);
        Encoding.ASCII.GetBytes("WAVE", h[8..]);
        Encoding.ASCII.GetBytes("fmt ", h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1);                       // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], 1);                       // mono
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], AudioChunk.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], AudioChunk.SampleRate * BytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], BytesPerSample);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], 16);
        Encoding.ASCII.GetBytes("data", h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], dataSize);
        _file.Write(h);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var data = (uint)Math.Min(SamplesWritten * BytesPerSample, uint.MaxValue - 36L);
        _file.Position = 0;
        WriteHeader(data + 36, data);
        _file.Dispose();
    }
}

/// <summary>Reads back what <see cref="WavWriter"/> wrote, by position.</summary>
public sealed class WavReader : IDisposable
{
    private readonly FileStream _file;
    private readonly long _dataStart;
    private byte[] _buffer = [];

    /// <exception cref="InvalidDataException">Not 16 kHz mono 16-bit PCM.</exception>
    public WavReader(string path)
    {
        _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            (_dataStart, SampleCount) = ReadHeader(_file);
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public long SampleCount { get; }

    public TimeSpan Duration => TimeSpan.FromSeconds((double)SampleCount / AudioChunk.SampleRate);

    /// <summary>Up to <paramref name="count"/> samples from <paramref name="start"/>, as floats.</summary>
    public float[] Read(long start, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (start >= SampleCount) return [];

        count = (int)Math.Min(count, SampleCount - start);
        var bytes = count * 2;
        if (_buffer.Length < bytes) _buffer = new byte[bytes];

        _file.Position = _dataStart + start * 2;
        _file.ReadExactly(_buffer, 0, bytes);

        var samples = new float[count];
        for (var i = 0; i < count; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(_buffer.AsSpan(i * 2)) / 32768f;
        }

        return samples;
    }

    private static (long Start, long Samples) ReadHeader(FileStream file)
    {
        Span<byte> riff = stackalloc byte[12];
        if (file.ReadAtLeast(riff, 12, throwOnEndOfStream: false) < 12
            || !riff[..4].SequenceEqual("RIFF"u8) || !riff[8..].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("Not a WAV file.");
        }

        Span<byte> chunk = stackalloc byte[8];
        Span<byte> format = stackalloc byte[16];
        var sawFormat = false;

        while (file.ReadAtLeast(chunk, 8, throwOnEndOfStream: false) == 8)
        {
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);

            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                file.ReadExactly(format);
                var ok = BinaryPrimitives.ReadUInt16LittleEndian(format) == 1
                         && BinaryPrimitives.ReadUInt16LittleEndian(format[2..]) == 1
                         && BinaryPrimitives.ReadUInt32LittleEndian(format[4..]) == AudioChunk.SampleRate
                         && BinaryPrimitives.ReadUInt16LittleEndian(format[14..]) == 16;
                if (!ok) throw new InvalidDataException("Expected 16 kHz mono 16-bit audio.");

                file.Seek(size - 16 + (size & 1), SeekOrigin.Current);
                sawFormat = true;
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (!sawFormat) throw new InvalidDataException("Audio data before its format.");

                var start = file.Position;
                var remaining = file.Length - start;

                // Zero is what a recording that never reached Dispose still says. Believing it
                // would turn an interrupted hour into nothing.
                var bytes = size == 0 || size > remaining ? remaining : size;
                return (start, bytes / 2);
            }
            else
            {
                file.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        throw new InvalidDataException("No audio data in the file.");
    }

    public void Dispose() => _file.Dispose();
}
