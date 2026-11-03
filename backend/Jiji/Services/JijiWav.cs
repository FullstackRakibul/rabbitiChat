// Jiji — PCM WAV. A part's duration is its real data size ÷ the byte rate in its fmt chunk:
// the file's length from the start of the "data" chunk to the end, never the size field in
// the header (a streamed WAV carries a placeholder there) and never the service's estimate.
// Every part must have the first part's format; narration.wav is written in that format.

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
                // The header's size is not trusted (0, 0xFFFFFFFF or a stale value in a streamed WAV):
                // the data is every byte from here to the end of the file, whole frames only.
                if (align is not > 0) throw new InvalidDataException("The block align is zero.");
                long bytes = wav.Length - body;
                bytes -= bytes % align.Value;
                return new JijiWavInfo(rate!.Value, channels!.Value, bits!.Value, byteRate.Value, align.Value, body, bytes);
            }
            pos = body + (int)size + (int)(size & 1);   // chunks are word-aligned
        }
        throw new InvalidDataException("No data chunk.");
    }

    /// <summary>Null when <paramref name="w"/> can be joined after <paramref name="first"/>; otherwise why not.</summary>
    public static string? FormatMismatch(JijiWavInfo first, JijiWavInfo w)
    {
        if (w.SampleRate == first.SampleRate && w.Channels == first.Channels && w.BitsPerSample == first.BitsPerSample) return null;
        return $"{w.SampleRate} Hz, {w.Channels} channel(s), {w.BitsPerSample}-bit, but the first part is "
             + $"{first.SampleRate} Hz, {first.Channels} channel(s), {first.BitsPerSample}-bit";
    }

    private static long GapBytes(JijiWavInfo format, int gapMs)
    {
        var gapBytes = (long)format.ByteRate * gapMs / 1000;
        return gapBytes - gapBytes % format.BlockAlign;
    }

    /// <summary>Joins parts of one format (the first part's), with <paramref name="gapMs"/> of silence between two parts.</summary>
    public static void Join(string outputPath, IReadOnlyList<(byte[] Bytes, JijiWavInfo Info)> parts, int gapMs)
    {
        if (parts.Count == 0) throw new InvalidDataException("Nothing to join.");
        var format = parts[0].Info;
        foreach (var (_, info) in parts)
            if (FormatMismatch(format, info) is { } why) throw new InvalidDataException(why);
        var gapBytes = GapBytes(format, gapMs);
        var dataBytes = parts.Sum(p => p.Info.DataBytes) + gapBytes * Math.Max(0, parts.Count - 1);
        if (dataBytes > uint.MaxValue - 36) throw new InvalidDataException("The narration is too long for one WAV file.");

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        WriteHeader(fs, format, (uint)dataBytes);
        var silence = new byte[gapBytes];
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) fs.Write(silence);
            var (bytes, info) = parts[i];
            fs.Write(bytes, (int)info.DataOffset, (int)info.DataBytes);
        }
    }

    /// <summary>The gap as it is really written: whole frames of <paramref name="format"/>.</summary>
    public static double GapMsExact(JijiWavInfo format, int gapMs) => GapBytes(format, gapMs) * 1000.0 / format.ByteRate;

    private static void WriteHeader(Stream s, JijiWavInfo f, uint dataBytes)
    {
        Span<byte> h = stackalloc byte[44];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h[4..], 36 + dataBytes);
        "WAVE"u8.CopyTo(h[8..]);
        "fmt "u8.CopyTo(h[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(h[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(h[22..], (ushort)f.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h[24..], (uint)f.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h[28..], (uint)f.ByteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(h[32..], (ushort)f.BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(h[34..], (ushort)f.BitsPerSample);
        "data"u8.CopyTo(h[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(h[40..], dataBytes);
        s.Write(h);
    }
}
