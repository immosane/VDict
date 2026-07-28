using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;

namespace VDict.RemediationFixture;

internal static class MDictFixtureWriter
{
    public static void Write(
        string path,
        ImmutableArray<(string Key, byte[] Record)> records,
        bool utf16Keys,
        string title,
        int? recordBlockSplitOffset = null)
    {
        if (records.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one MDict record is required.", nameof(records));
        }

        Encoding keyEncoding = utf16Keys ? Encoding.Unicode : Encoding.UTF8;
        byte[] delimiter = utf16Keys ? [0, 0] : [0];
        using MemoryStream keyData = new();
        using MemoryStream recordData = new();
        foreach ((string key, byte[] record) in records)
        {
            WriteBigEndian(keyData, checked((uint)recordData.Length));
            keyData.Write(keyEncoding.GetBytes(key));
            keyData.Write(delimiter);
            recordData.Write(record);
        }

        byte[] keyBlock = WrapUncompressed(keyData.ToArray());
        string first = records[0].Key;
        string last = records[^1].Key;
        using MemoryStream keyInfo = new();
        WriteBigEndian(keyInfo, checked((uint)records.Length));
        keyInfo.WriteByte(checked((byte)first.Length));
        keyInfo.Write(keyEncoding.GetBytes(first));
        keyInfo.WriteByte(checked((byte)last.Length));
        keyInfo.Write(keyEncoding.GetBytes(last));
        WriteBigEndian(keyInfo, checked((uint)keyBlock.Length));
        WriteBigEndian(keyInfo, checked((uint)keyData.Length));

        byte[] recordBytes = recordData.ToArray();
        byte[][] recordPayloads = recordBlockSplitOffset is > 0 &&
                                  recordBlockSplitOffset < recordBytes.Length
            ? [recordBytes[..recordBlockSplitOffset.Value],
               recordBytes[recordBlockSplitOffset.Value..]]
            : [recordBytes];
        byte[][] recordBlocks = recordPayloads.Select(WrapUncompressed).ToArray();
        string encoding = utf16Keys ? "UTF-16" : "UTF-8";
        string headerText =
            $"""<Dictionary GeneratedByEngineVersion="1.2" Encoding="{encoding}" Title="{title}" />""";
        byte[] header = Encoding.Unicode.GetBytes(headerText + "\0");
        using FileStream output = File.Create(path);
        WriteBigEndian(output, checked((uint)header.Length));
        output.Write(header);
        WriteBigEndian(output, 0);
        WriteBigEndian(output, 1);
        WriteBigEndian(output, checked((uint)records.Length));
        WriteBigEndian(output, checked((uint)keyInfo.Length));
        WriteBigEndian(output, checked((uint)keyBlock.Length));
        keyInfo.Position = 0;
        keyInfo.CopyTo(output);
        output.Write(keyBlock);
        WriteBigEndian(output, checked((uint)recordBlocks.Length));
        WriteBigEndian(output, checked((uint)records.Length));
        WriteBigEndian(output, checked((uint)(recordBlocks.Length * 8)));
        WriteBigEndian(output, checked((uint)recordBlocks.Sum(block => block.Length)));
        for (int index = 0; index < recordBlocks.Length; index++)
        {
            WriteBigEndian(output, checked((uint)recordBlocks[index].Length));
            WriteBigEndian(output, checked((uint)recordPayloads[index].Length));
        }
        foreach (byte[] recordBlock in recordBlocks)
        {
            output.Write(recordBlock);
        }
    }

    private static byte[] WrapUncompressed(byte[] bytes)
    {
        byte[] output = new byte[8 + bytes.Length];
        bytes.CopyTo(output, 8);
        return output;
    }

    private static void WriteBigEndian(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
