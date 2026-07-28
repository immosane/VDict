using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;

namespace VDict.Infrastructure.Tests.Fixtures;

public sealed class BrowserAssetFixtureTests
{
    [Fact]
    public void RasterAndVectorFixtures_AreStructurallyDecodable()
    {
        ReadOnlySpan<byte> png = BrowserAssetFixtures.Png;
        Assert.True(png[..8].SequenceEqual(
            new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }));

        byte[] imageData = ReadPngImageData(png, out int width, out int height);
        Assert.Equal(2, width);
        Assert.Equal(2, height);
        using MemoryStream compressed = new(imageData);
        using ZLibStream inflater = new(compressed, CompressionMode.Decompress);
        using MemoryStream pixels = new();
        inflater.CopyTo(pixels);
        Assert.Equal(height * (1 + (width * 4)), pixels.Length);

        XDocument svg = XDocument.Parse(BrowserAssetFixtures.Svg);
        Assert.Equal("svg", svg.Root?.Name.LocalName);
        Assert.Equal("24", svg.Root?.Attribute("width")?.Value);
    }

    [Fact]
    public void FontFixtures_HaveCompleteContainerTables()
    {
        ReadOnlySpan<byte> ttf = BrowserAssetFixtures.Ttf;
        Assert.Equal(0x00010000u, BinaryPrimitives.ReadUInt32BigEndian(ttf));
        int tableCount = BinaryPrimitives.ReadUInt16BigEndian(ttf[4..]);
        Assert.InRange(tableCount, 8, 128);
        for (int index = 0; index < tableCount; index++)
        {
            ReadOnlySpan<byte> record = ttf.Slice(12 + (index * 16), 16);
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            Assert.True(offset <= ttf.Length && length <= ttf.Length - offset);
        }

        ReadOnlySpan<byte> woff2 = BrowserAssetFixtures.Woff2;
        Assert.True(woff2[..4].SequenceEqual("wOF2"u8));
        Assert.Equal(woff2.Length, BinaryPrimitives.ReadInt32BigEndian(woff2[8..]));
        Assert.True(BinaryPrimitives.ReadUInt32BigEndian(woff2[16..]) > woff2.Length);
        Assert.True(BinaryPrimitives.ReadUInt16BigEndian(woff2[12..]) >= 8);
    }

    [Fact]
    public void AudioFixtures_AreCompletePcmAndMpegStreams()
    {
        ReadOnlySpan<byte> wav = BrowserAssetFixtures.Wav;
        Assert.True(wav[..4].SequenceEqual("RIFF"u8));
        Assert.True(wav[8..12].SequenceEqual("WAVE"u8));
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav[4..]));
        Assert.True(wav[12..].IndexOf("fmt "u8) >= 0);
        Assert.True(wav[12..].IndexOf("data"u8) >= 0);

        ReadOnlySpan<byte> mp3 = BrowserAssetFixtures.Mp3;
        int[] bitratesKbps =
            [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        int[] sampleRates = [44_100, 48_000, 32_000];
        int offset = 0;
        int frames = 0;
        while (offset < mp3.Length)
        {
            Assert.True(mp3[offset] == 0xff && (mp3[offset + 1] & 0xe0) == 0xe0);
            Assert.Equal(3, (mp3[offset + 1] >> 3) & 0x03); // MPEG-1.
            Assert.Equal(1, (mp3[offset + 1] >> 1) & 0x03); // Layer III.
            int bitrateIndex = (mp3[offset + 2] >> 4) & 0x0f;
            int sampleRateIndex = (mp3[offset + 2] >> 2) & 0x03;
            Assert.InRange(bitrateIndex, 1, 14);
            Assert.InRange(sampleRateIndex, 0, 2);
            int padding = (mp3[offset + 2] >> 1) & 1;
            int frameLength =
                ((144 * bitratesKbps[bitrateIndex] * 1_000) /
                 sampleRates[sampleRateIndex]) + padding;
            offset += frameLength;
            frames++;
        }

        Assert.Equal(mp3.Length, offset);
        Assert.Equal(82, frames);
    }

    [Fact]
    public void ScriptModuleAndJsonFixtures_AreExecutableOfflineInputs()
    {
        Assert.Contains("window.vdictFixture", BrowserAssetFixtures.Script);
        Assert.Contains("export const", BrowserAssetFixtures.Module);
        Assert.Contains("import", BrowserAssetFixtures.Module);
        using JsonDocument json = JsonDocument.Parse(BrowserAssetFixtures.Json);
        Assert.Equal("offline", json.RootElement.GetProperty("mode").GetString());
    }

    private static byte[] ReadPngImageData(
        ReadOnlySpan<byte> png,
        out int width,
        out int height)
    {
        width = 0;
        height = 0;
        using MemoryStream idat = new();
        int offset = 8;
        while (offset < png.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(png[offset..]);
            ReadOnlySpan<byte> type = png.Slice(offset + 4, 4);
            ReadOnlySpan<byte> data = png.Slice(offset + 8, length);
            uint expectedCrc = BinaryPrimitives.ReadUInt32BigEndian(
                png.Slice(offset + 8 + length, 4));
            Assert.Equal(expectedCrc, ComputeCrc32(type, data));
            if (type.SequenceEqual("IHDR"u8))
            {
                width = BinaryPrimitives.ReadInt32BigEndian(data);
                height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                Assert.Equal(8, data[8]);
                Assert.Equal(6, data[9]);
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                break;
            }

            offset += 12 + length;
        }

        Assert.True(width > 0 && height > 0);
        return idat.ToArray();
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
}
