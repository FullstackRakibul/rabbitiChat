// Jiji — PCM WAV, read from its own header. A part's duration is its data bytes ÷ the
// byte rate in its fmt chunk, never the speech service's estimate, and every part must
// be the one format narration.wav is joined in (24 kHz, 16-bit, mono PCM).

using System.Buffers.Binary;
using System.Text;

namespace RabbitIC.Api.Jiji.Services;

public sealed record JijiWavInfo(int SampleRate, int Channels, int BitsPerSample, int ByteRate, int BlockAlign,
    long DataOffset, long DataBytes)
{
    public double DurationMs => DataBytes * 1000.0 / ByteRate;
}

public static class JijiWav
{
    public const int SampleRate = 24_000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;

    /// <summary>The header of a RIFF/WAVE file. Throws <see cref="InvalidDataException"/> when it is not one.</summary>
    public static JijiWavInfo Read(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        int? rate = null, channels = null, bits = null, byteRate = null, align = null, format = null;
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav.Slice(pos, 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(pos + 4, 4));
            var body = pos + 8;
            if (id == "fmt ")
            {
                if (size < 16 || body + 16 > wav.Length) throw new InvalidDataException("The fmt chunk is short.");
                var f = wav.Slice(body, 16);
                format = BinaryPrimitives.ReadUInt16LittleEndian(f);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(f[2..]);
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[4..]);
                byteRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(f[8..]);
                align = BinaryPrimitives.ReadUInt16LittleEndian(f[12..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(f[14..]);
            }
            else if (id == "data")
            {
                if (format is null) throw new InvalidDataException("The data chunk comes before fmt.");
                if (format != 1) throw new InvalidDataException($"Not PCM (format {format}).");
                if (byteRate is not > 0) throw new InvalidDataException("The byte rate is zero.");
                // A streamed header may carry 0 or 0xFFFFFFFF; the bytes that are there are the data.
                long available = wav.Length - body;
                long bytes = size is 0 or uint.MaxValue || size > available ? available : size;
                bytes -= bytes % align!.Value;
                return new JijiWavInfo(rate!.Value, channels!.Value, bits!.Value, byteRate.Value, align.Value, body, bytes);
            }
            pos = body + (int)size + (int)(size & 1);   // chunks are word-aligned
        }
        throw new InvalidDataException("No data chunk.");
    }

    public static void RequireNarrationFormat(JijiWavInfo w)
    {
        if (w.SampleRate != SampleRate || w.Channels != Channels || w.BitsPerSample != BitsPerSample)
            throw new InvalidDataException($"Expected {SampleRate} Hz, {BitsPerSample}-bit, mono; got {w.SampleRate} Hz, {w.BitsPerSample}-bit, {w.Channels} channel(s).");
    }

    /// <summary>Joins parts of the narration format, with <paramref name="gapMs"/> of silence between two parts.</summary>
    public static void Join(string outputPath, IReadOnlyList<(byte[] Bytes, JijiWavInfo Info)> parts, int gapMs)
    {
        var gapBytes = (long)SampleRate * Channels * (BitsPerSample / 8) * gapMs / 1000;
        gapBytes -= gapBytes % (Channels * BitsPerSample / 8);
        var dataBytes = parts.Sum(p => p.Info.DataBytes) + gapBytes * Math.Max(0, parts.Count - 1);
        if (dataBytes > uint.MaxValue - 36) throw new InvalidDataException("The narration is too long for one WAV file.");

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        WriteHeader(fs, (uint)dataBytes);
        var silence = new byte[gapBytes];
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) fs.Write(silence);
            var (bytes, info) = parts[i];
            fs.Write(bytes, (int)info.DataOffset, (int)info.DataBytes);
        }
    }

    public static double GapMsExact(int gapMs)
    {
        var block = Channels * BitsPerSample / 8;
        var gapBytes = (long)SampleRate * block * gapMs / 1000;
        gapBytes -= gapBytes % block;
        return gapBytes * 1000.0 / (SampleRate * block);
    }

    private static void WriteHeader(Stream s, uint dataBytes)
    {
        Span<byte> h = stackalloc byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)(SampleRate * Channels * BitsPerSample / 8));
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], BitsPerSample);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], dataBytes);
        s.Write(h);
    }
}
