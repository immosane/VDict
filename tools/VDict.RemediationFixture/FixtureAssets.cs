using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace VDict.RemediationFixture;

internal static class FixtureAssets
{
    public static byte[] Png { get; } = CreatePng();

    public static byte[] Jpeg { get; } = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAf/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9k=");

    public static byte[] Gif { get; } = Convert.FromBase64String(
        "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==");

    public static byte[] WebP { get; } = Convert.FromBase64String(
        "UklGRiIAAABXRUJQVlA4IBYAAAAwAQCdASoBAAEAAUAmJaQAA3AA/v89WAAAAA==");

    public static byte[] Wave { get; } = CreateWave();

    public static byte[] Svg { get; } = Encoding.UTF8.GetBytes(
        """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24"><circle cx="12" cy="12" r="10" fill="#287a45"/><path d="M7 12h10" stroke="white" stroke-width="2"/></svg>""");

    private static byte[] CreatePng()
    {
        byte[] scanlines =
        [
            0, 0xff, 0x20, 0x20, 0xff, 0x20, 0xff, 0x20, 0xff,
            0, 0x20, 0x20, 0xff, 0xff, 0xff, 0xd0, 0x20, 0xff
        ];
        using MemoryStream compressed = new();
        using (ZLibStream encoder = new(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            encoder.Write(scanlines);
        }

        using MemoryStream png = new();
        png.Write([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, 2);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], 2);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(png, "IHDR"u8, header);
        WritePngChunk(png, "IDAT"u8, compressed.ToArray());
        WritePngChunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void WritePngChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        output.Write(type);
        output.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, ComputeCrc32(type, data));
        output.Write(number);
    }

    private static uint ComputeCrc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        uint crc = 0xffffffff;
        foreach (byte value in first)
        {
            crc = UpdateCrc32(crc, value);
        }

        foreach (byte value in second)
        {
            crc = UpdateCrc32(crc, value);
        }

        return ~crc;
    }

    private static uint UpdateCrc32(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
        }

        return crc;
    }

    private static byte[] CreateWave()
    {
        const int sampleRate = 8_000;
        const int sampleCount = sampleRate / 8;
        const short channels = 1;
        const short bitsPerSample = 16;
        int dataLength = sampleCount * sizeof(short);
        using MemoryStream output = new();
        using BinaryWriter writer = new(output, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataLength);
        for (int sample = 0; sample < sampleCount; sample++)
        {
            double phase = 2 * Math.PI * 440 * sample / sampleRate;
            writer.Write((short)(Math.Sin(phase) * short.MaxValue * 0.15));
        }

        return output.ToArray();
    }
}
