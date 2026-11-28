using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace ArtMockups;

/// <summary>
/// Print metadata that OpenCV does not write: the resolution (PNG pHYs / JPEG JFIF density) and the embedded ICC colour
/// profile. Only the file headers are edited; the pixel data is left exactly as encoded.
/// </summary>
public static class PrintMetadata
{
    static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];
    static ReadOnlySpan<byte> IccTag => "ICC_PROFILE\0"u8;
    const int MaxIccPerSegment = 65535 - 2 - 14;

    static bool IsPng(ReadOnlySpan<byte> d) => d.StartsWith(PngSignature);
    static bool IsJpeg(ReadOnlySpan<byte> d) => d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8;

    /// <summary>
    /// The ICC profile embedded in a PNG, JPEG or WebP file, or null. Only RGB profiles are returned, because the pieces
    /// are always written as RGB (a CMYK or grayscale profile would no longer describe them).
    /// </summary>
    public static byte[]? ReadRgbProfile(byte[] file)
    {
        byte[]? icc;
        try { icc = IsPng(file) ? FromPng(file) : IsJpeg(file) ? FromJpeg(file) : FromWebp(file); }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IndexOutOfRangeException) { return null; }
        return icc is { Length: > 128 } && icc.AsSpan(16, 4).SequenceEqual("RGB "u8) ? icc : null;
    }

    /// <summary>Returns a copy of an encoded PNG or JPEG tagged with the given DPI and (optionally) ICC profile.</summary>
    public static byte[] Apply(byte[] encoded, double dpiX, double dpiY, byte[]? icc)
    {
        if (IsPng(encoded)) return ApplyPng(encoded, dpiX, dpiY, icc);
        if (IsJpeg(encoded)) return ApplyJpeg(encoded, dpiX, dpiY, icc);
        throw new ArgumentException("Os metadados podem ser gravados em PNG e JPEG.");
    }

    // ---------- PNG ----------

    static IEnumerable<(string Type, int Start, int Length)> Chunks(byte[] png)
    {
        for (int p = 8; p + 12 <= png.Length;)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(p));
            if (len < 0 || len > png.Length - p - 12) yield break;
            yield return (Encoding.ASCII.GetString(png, p + 4, 4), p, len);
            p += 12 + len;
        }
    }

    static byte[]? FromPng(byte[] png)
    {
        foreach (var (type, start, len) in Chunks(png))
        {
            if (type != "iCCP") continue;
            var data = png.AsSpan(start + 8, len);
            int nameEnd = data.IndexOf((byte)0);
            if (nameEnd < 0) return null;
            using var zip = new ZLibStream(new MemoryStream(data[(nameEnd + 2)..].ToArray()), CompressionMode.Decompress);
            using var result = new MemoryStream();
            zip.CopyTo(result);
            return result.ToArray();
        }
        return null;
    }

    static byte[] ApplyPng(byte[] png, double dpiX, double dpiY, byte[]? icc)
    {
        var output = new MemoryStream(png.Length + (icc?.Length ?? 0) + 64);
        output.Write(PngSignature);
        foreach (var (type, start, len) in Chunks(png))
        {
            if (type == "pHYs" || (icc != null && type is "iCCP" or "sRGB")) continue;
            output.Write(png, start, 12 + len);
            if (type != "IHDR") continue;

            // Both chunks must come before the pixel data, so they go right after the header.
            if (icc != null)
            {
                var body = new MemoryStream();
                body.Write("ICC profile\0\0"u8); // name, terminator, compression method 0 (zlib)
                using (var zip = new ZLibStream(body, CompressionLevel.Optimal, leaveOpen: true)) zip.Write(icc);
                WriteChunk(output, "iCCP", body.ToArray());
            }
            var phys = new byte[9];
            BinaryPrimitives.WriteUInt32BigEndian(phys, (uint)Math.Round(dpiX / 0.0254)); // pixels per metre
            BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), (uint)Math.Round(dpiY / 0.0254));
            phys[8] = 1; // unit: metre
            WriteChunk(output, "pHYs", phys);
        }
        return output.ToArray();
    }

    static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, ~Crc(Crc(0xFFFFFFFF, typeBytes), data));
        output.Write(number);
    }

    static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }
        return crc;
    }

    // ---------- JPEG ----------

    static byte[]? FromJpeg(byte[] jpg)
    {
        var parts = new SortedDictionary<int, byte[]>();
        for (int p = 2; p + 4 <= jpg.Length && jpg[p] == 0xFF;)
        {
            byte marker = jpg[p + 1];
            if (marker is 0xDA or 0xD9) break; // pixel data starts
            int len = BinaryPrimitives.ReadUInt16BigEndian(jpg.AsSpan(p + 2));
            if (len < 2 || p + 2 + len > jpg.Length) break;
            var body = jpg.AsSpan(p + 4, len - 2);
            if (marker == 0xE2 && body.Length > 14 && body.StartsWith(IccTag)) parts[body[12]] = body[14..].ToArray();
            p += 2 + len;
        }
        return parts.Count == 0 ? null : parts.Values.SelectMany(x => x).ToArray();
    }

    static byte[] ApplyJpeg(byte[] jpg, double dpiX, double dpiY, byte[]? icc)
    {
        bool hasJfif = jpg.Length > 20 && jpg[2] == 0xFF && jpg[3] == 0xE0 && jpg.AsSpan(6, 5).SequenceEqual("JFIF\0"u8);
        int rest = hasJfif ? 4 + BinaryPrimitives.ReadUInt16BigEndian(jpg.AsSpan(4)) : 2;

        var app0 = hasJfif
            ? jpg.AsSpan(2, rest - 2).ToArray()
            : [0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 0, 0, 0, 0, 0];
        app0[11] = 1; // unit: dots per inch
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(12), (ushort)Math.Clamp(Math.Round(dpiX), 1, ushort.MaxValue));
        BinaryPrimitives.WriteUInt16BigEndian(app0.AsSpan(14), (ushort)Math.Clamp(Math.Round(dpiY), 1, ushort.MaxValue));

        var output = new MemoryStream(jpg.Length + (icc?.Length ?? 0) + 64);
        output.Write(jpg, 0, 2);
        output.Write(app0);
        if (icc != null)
        {
            int count = (icc.Length + MaxIccPerSegment - 1) / MaxIccPerSegment;
            if (count > 255) throw new InvalidDataException("O perfil de cor excede o limite do JPEG. Exporte como PNG.");
            Span<byte> header = stackalloc byte[4];
            for (int i = 0; i < count; i++)
            {
                int start = i * MaxIccPerSegment, len = Math.Min(MaxIccPerSegment, icc.Length - start);
                header[0] = 0xFF; header[1] = 0xE2;
                BinaryPrimitives.WriteUInt16BigEndian(header[2..], (ushort)(len + 16));
                output.Write(header);
                output.Write(IccTag);
                output.WriteByte((byte)(i + 1));
                output.WriteByte((byte)count);
                output.Write(icc, start, len);
            }
        }
        output.Write(jpg, rest, jpg.Length - rest);
        return output.ToArray();
    }

    // ---------- WebP ----------

    static byte[]? FromWebp(byte[] d)
    {
        if (d.Length < 12 || !d.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !d.AsSpan(8, 4).SequenceEqual("WEBP"u8)) return null;
        for (int p = 12; p + 8 <= d.Length;)
        {
            int len = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p + 4));
            if (len < 0 || len > d.Length - p - 8) break;
            if (d.AsSpan(p, 4).SequenceEqual("ICCP"u8)) return d.AsSpan(p + 8, len).ToArray();
            p += 8 + len + (len & 1);
        }
        return null;
    }
}
