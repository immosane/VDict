using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using VDict.Infrastructure.Catalog;

namespace VDict.Infrastructure.DictionaryFormats;

public sealed class MDictReader : IDisposable
{
    private const int MaximumKeyBlockInfoBytes = 64 * 1024 * 1024;
    private const int MaximumKeySectionBytes = 256 * 1024 * 1024;
    private const int MaximumCompressedBlockBytes = 64 * 1024 * 1024;
    private const int MaximumDecompressedBlockBytes = 64 * 1024 * 1024;
    private const int MaximumMaterializedRecordBytes = 64 * 1024 * 1024;
    private const int MaximumRecordBlockCount = 1_000_000;
    private const int MaximumKeyBlockCount = 1_000_000;
    private readonly FileStream _stream;
    private readonly int _numberWidth;
    private readonly bool _loadKeys;
    private readonly MDictKeyLoadMode _keyLoadMode;
    private readonly int _encrypt;
    private readonly Dictionary<string, (long Offset, long Length)> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Offset, long Length)> _recordsByFileName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ambiguousFileNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MDictEntry> _entries = [];
    private readonly List<RecordBlockInfo> _recordBlocks = [];
    private readonly DictionaryResourceCache? _resourceCache;
    private readonly DictionarySourceIdentity _recordCacheSource;
    private readonly MddResourceIndex? _externalResourceIndex;
    private long _recordBlockOffset;

    public MDictReader(string path, bool loadKeys)
        : this(path, loadKeys, null, null)
    {
    }

    internal MDictReader(
        string path,
        bool loadKeys,
        DictionaryResourceCache? resourceCache,
        MddResourceIndex? externalResourceIndex = null,
        MDictKeyLoadMode keyLoadMode = MDictKeyLoadMode.Full)
    {
        Path = path;
        _loadKeys = loadKeys;
        _keyLoadMode = keyLoadMode;
        _resourceCache = resourceCache;
        _recordCacheSource = resourceCache?.GetRecordSourceIdentity(path) ??
            DictionarySourceIdentity.FromPath(path);
        _externalResourceIndex = externalResourceIndex;
        _stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        try
        {
            Header = MDictHeaderReader.Read(_stream);
            Version = double.Parse(Header.GetValueOrDefault("GeneratedByEngineVersion", "2.0"), System.Globalization.CultureInfo.InvariantCulture);
            EncodingName = path.EndsWith(".mdd", StringComparison.OrdinalIgnoreCase)
                ? "UTF-16"
                : NormalizeEncoding(Header.GetValueOrDefault("Encoding", "UTF-8"));
            _encrypt = Header.TryGetValue("Encrypted", out var encrypted)
                ? encrypted.Equals("Yes", StringComparison.OrdinalIgnoreCase) ? 1 : int.TryParse(encrypted, out var flag) ? flag : 0
                : 0;
            _numberWidth = Version < 2.0 ? 4 : 8;
            ReadKeySection();
            BuildRecordBlockIndex();
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public string Path { get; }

    public Dictionary<string, string> Header { get; }

    public double Version { get; }

    public string EncodingName { get; }

    public IReadOnlyList<MDictEntry> Entries => _entries;

    internal MddResourceProfile ResourceProfile { get; } = new();

    internal MddResourceIndex ResourceIndex { get; } = new();

    public byte[] ReadRecord(long offset, long length)
    {
        return ReadRecord(offset, length, out _);
    }

    public string ReadTextRecord(long offset, long length)
    {
        var bytes = ReadRecord(offset, length);
        return TextDecoding.Decode(bytes, EncodingName).Trim('\0').Trim();
    }

    public byte[]? ReadResource(string path) => ReadResource(path, out _, out _);

    internal byte[]? ReadResource(string path, out string? lookupKey)
    {
        return ReadResource(path, out lookupKey, out _);
    }

    internal byte[]? ReadResource(
        string path,
        out string? lookupKey,
        out MDictRecordReadDiagnostics readDiagnostics)
    {
        lookupKey = null;
        readDiagnostics = MDictRecordReadDiagnostics.None;
        var normalized = DictionaryStore.NormalizeResourcePath(path);
        var name = System.IO.Path.GetFileName(normalized);
        var candidates = new[]
        {
            normalized,
            "\\" + normalized.Replace('/', '\\'),
            name,
            "audio/" + name,
            "sound/" + name,
            "sounds/" + name,
            "mp3/" + name,
            "media/" + name
        };

        foreach (var candidate in candidates.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_records.TryGetValue(DictionaryStore.NormalizeResourcePath(candidate), out var record))
            {
                lookupKey = candidate;
                return ReadRecord(record.Offset, record.Length, out readDiagnostics);
            }
            if (_externalResourceIndex?.TryGet(candidate, out record) == true)
            {
                lookupKey = candidate;
                return ReadRecord(record.Offset, record.Length, out readDiagnostics);
            }
            if (ResourceIndex.TryGet(candidate, out record))
            {
                lookupKey = candidate;
                return ReadRecord(record.Offset, record.Length, out readDiagnostics);
            }
        }

        if (!_ambiguousFileNames.Contains(name) &&
            _recordsByFileName.TryGetValue(name, out var byName))
        {
            lookupKey = name;
            return ReadRecord(byName.Offset, byName.Length, out readDiagnostics);
        }

        return null;
    }

    private void ReadKeySection()
    {
        if (Version >= 3.0)
        {
            ReadKeySectionV3();
            return;
        }

        ReadKeySectionV1V2();
    }

    private void ReadKeySectionV1V2()
    {
        _ = ReadNumber();
        _ = ReadNumber();
        if (Version >= 2.0)
        {
            _ = ReadNumber();
        }

        var keyBlockInfoSize = ReadNumber();
        var keyBlockSize = ReadNumber();
        if (Version >= 2.0)
        {
            _ = ReadUInt32BigEndian();
        }

        if (_loadKeys)
        {
            var keyBlockInfo = ReadExactly(CheckedSize(
                keyBlockInfoSize,
                MaximumKeyBlockInfoBytes,
                "key block info"));
            var keyBlockInfos = DecodeKeyBlockInfo(keyBlockInfo);
            var keyBlocks = ReadExactly(CheckedSize(
                keyBlockSize,
                MaximumKeySectionBytes,
                "key section"));
            DecodeKeyBlocks(keyBlocks, keyBlockInfos);
        }
        else
        {
            ValidateFileRange(_stream.Position, keyBlockInfoSize, "key block info");
            _stream.Seek(keyBlockInfoSize, SeekOrigin.Current);
            ValidateFileRange(_stream.Position, keyBlockSize, "key section");
            _stream.Seek(keyBlockSize, SeekOrigin.Current);
        }

        _recordBlockOffset = _stream.Position;
    }

    private void ReadKeySectionV3()
    {
        while (_stream.Position < _stream.Length)
        {
            var blockType = ReadUInt32BigEndian();
            var blockSize = ReadNumber();
            var blockOffset = _stream.Position;
            ValidateFileRange(blockOffset, blockSize, "version 3 section");
            if (blockType == 0x01000000)
            {
                _recordBlockOffset = blockOffset;
            }
            else if (blockType == 0x03000000 && _loadKeys)
            {
                DecodeKeyDataV3(blockOffset);
            }

            _stream.Position = checked(blockOffset + blockSize);
        }
    }

    private void DecodeKeyDataV3(long blockOffset)
    {
        var old = _stream.Position;
        _stream.Position = blockOffset;
        var number = ReadUInt32BigEndian();
        if (number > MaximumKeyBlockCount)
        {
            throw new InvalidDataException($"MDict key block count is too large: {number}.");
        }

        _ = ReadNumber();
        var offsets = new List<(long Offset, byte[] Key)>();
        for (var i = 0; i < number; i++)
        {
            var decompressedSize = ReadUInt32BigEndian();
            var compressedSize = ReadUInt32BigEndian();
            var block = DecodeBlock(
                ReadExactly(CheckedSize(
                    compressedSize,
                    MaximumCompressedBlockBytes,
                    "compressed key block")),
                decompressedSize);
            offsets.AddRange(SplitKeyBlock(block));
        }

        LoadRecordMap(offsets);
        _stream.Position = old;
    }

    private List<(long CompressedSize, long DecompressedSize)> DecodeKeyBlockInfo(byte[] compressed)
    {
        byte[] info;
        if (Version >= 2.0)
        {
            if (compressed.Length < 8 || compressed[0] != 0x02)
            {
                throw new NotSupportedException("Encrypted or unsupported key block info is not supported yet.");
            }

            if ((_encrypt & 0x02) != 0)
            {
                var keySeed = compressed.AsSpan(4, 4).ToArray().Concat(BitConverter.GetBytes(0x3695u)).ToArray();
                var key = Ripemd128.Hash(keySeed);
                var decrypted = FastDecrypt(compressed.AsSpan(8).ToArray(), key);
                info = ZlibDecompress(decrypted);
            }
            else
            {
                info = ZlibDecompress(compressed.AsSpan(8).ToArray());
            }
        }
        else
        {
            info = compressed;
        }

        var output = new List<(long CompressedSize, long DecompressedSize)>();
        var index = 0;
        while (index < info.Length)
        {
            index += _numberWidth;
            var textSizeWidth = Version >= 2.0 ? 2 : 1;
            var textTerm = Version >= 2.0 ? 1 : 0;
            var headSize = ReadInlineInteger(info, index, textSizeWidth);
            index += textSizeWidth + TextByteCount(headSize + textTerm);
            var tailSize = ReadInlineInteger(info, index, textSizeWidth);
            index += textSizeWidth + TextByteCount(tailSize + textTerm);
            var compressedSize = ReadInlineNumber(info, index);
            index += _numberWidth;
            var decompressedSize = ReadInlineNumber(info, index);
            index += _numberWidth;
            output.Add((compressedSize, decompressedSize));
        }

        return output;
    }

    private void DecodeKeyBlocks(byte[] compressed, List<(long CompressedSize, long DecompressedSize)> infos)
    {
        var keyOffsets = new List<(long Offset, byte[] Key)>();
        var index = 0;
        foreach (var info in infos)
        {
            var block = DecodeBlock(compressed.AsSpan(index, CheckedInt(info.CompressedSize)).ToArray(), info.DecompressedSize);
            keyOffsets.AddRange(SplitKeyBlock(block));
            index += CheckedInt(info.CompressedSize);
        }

        LoadRecordMap(keyOffsets);
    }

    private List<(long Offset, byte[] Key)> SplitKeyBlock(byte[] block)
    {
        var output = new List<(long Offset, byte[] Key)>();
        var index = 0;
        var delimiter = EncodingName.Equals("UTF-16", StringComparison.OrdinalIgnoreCase) ? new byte[] { 0, 0 } : new byte[] { 0 };
        var charWidth = delimiter.Length;
        while (index < block.Length)
        {
            var offset = ReadInlineNumber(block, index);
            index += _numberWidth;
            var end = index;
            while (end + charWidth <= block.Length && !block.AsSpan(end, charWidth).SequenceEqual(delimiter))
            {
                end += charWidth;
            }

            var keyBytes = block.AsSpan(index, Math.Max(0, end - index)).ToArray();
            output.Add((offset, DecodeKeyBytes(keyBytes)));
            index = end + charWidth;
        }

        return output;
    }

    private void LoadRecordMap(List<(long Offset, byte[] Key)> offsets)
    {
        _entries.Clear();
        for (var i = 1; i < offsets.Count; i++)
        {
            if (offsets[i].Offset < offsets[i - 1].Offset)
            {
                throw new InvalidDataException("MDict record offsets are not monotonically increasing.");
            }
        }

        for (var i = 0; i < offsets.Count; i++)
        {
            var key = TextDecoding.Decode(offsets[i].Key).Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var length = i + 1 < offsets.Count ? offsets[i + 1].Offset - offsets[i].Offset : -1;
            if (_keyLoadMode != MDictKeyLoadMode.CompactResourcesOnly)
            {
                _entries.Add(new MDictEntry(key, offsets[i].Key, offsets[i].Offset, length));
            }
            if (_keyLoadMode == MDictKeyLoadMode.EntriesOnly)
            {
                continue;
            }

            var normalized = DictionaryStore.NormalizeResourcePath(key);
            if (_keyLoadMode == MDictKeyLoadMode.Full)
            {
                _records[normalized] = (offsets[i].Offset, length);
                var fileName = normalized.Split('/').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(fileName))
                {
                    if (!_recordsByFileName.TryAdd(fileName, (offsets[i].Offset, length)))
                    {
                        _ambiguousFileNames.Add(fileName);
                    }
                }
            }
            ResourceProfile.Observe(normalized);
            ResourceIndex.Add(normalized, offsets[i].Offset, length);
        }
    }

    private byte[] DecodeKeyBytes(byte[] keyBytes)
    {
        return Encoding.UTF8.GetBytes(TextDecoding.Decode(keyBytes, EncodingName).Trim('\0').Trim());
    }

    private byte[] ReadRecord(
        long offset,
        long length,
        out MDictRecordReadDiagnostics readDiagnostics)
        => ReadIndexedRecord(offset, length, out readDiagnostics);

    private byte[] ReadIndexedRecord(
        long offset,
        long length,
        out MDictRecordReadDiagnostics readDiagnostics)
    {
        if (offset < 0 || length < -1)
        {
            throw new InvalidDataException("MDict record range is invalid.");
        }

        if (_recordBlocks.Count == 0)
        {
            throw new InvalidDataException("MDict does not contain any record blocks.");
        }

        RecordBlockInfo last = _recordBlocks[^1];
        long streamEnd = checked(last.DecompressedOffset + last.DecompressedSize);
        long requestedLength = length >= 0 ? length : streamEnd - offset;
        int resultLength = CheckedSize(
            requestedLength,
            MaximumMaterializedRecordBytes,
            "materialized record");
        if (offset > streamEnd || requestedLength > streamEnd - offset)
        {
            throw new InvalidDataException("Record range is outside the MDict record blocks.");
        }

        if (resultLength == 0)
        {
            readDiagnostics = MDictRecordReadDiagnostics.None;
            return [];
        }

        int blockIndex = FindRecordBlockIndex(offset);
        if (blockIndex < 0)
        {
            throw new InvalidDataException("Record offset is outside the MDict record blocks.");
        }

        byte[] result = new byte[resultLength];
        int written = 0;
        long sourceBytesRead = 0;
        long decodedBytes = 0;
        bool allBlocksFromCache = true;
        while (written < result.Length && blockIndex < _recordBlocks.Count)
        {
            RecordBlockInfo info = _recordBlocks[blockIndex];
            byte[] block = ReadRecordBlock(info, out bool cacheHit);
            allBlocksFromCache &= cacheHit;
            if (!cacheHit)
            {
                sourceBytesRead += info.CompressedSize;
                decodedBytes += info.DecompressedSize;
            }

            int start = CheckedInt(offset + written - info.DecompressedOffset);
            if (start < 0 || start > block.Length)
            {
                throw new InvalidDataException("MDict record block offset is invalid.");
            }

            int count = Math.Min(result.Length - written, block.Length - start);
            if (count <= 0)
            {
                break;
            }

            block.AsSpan(start, count).CopyTo(result.AsSpan(written));
            written += count;
            blockIndex++;
        }

        if (written != result.Length)
        {
            throw new InvalidDataException(
                $"MDict record is truncated: expected {result.Length} bytes, read {written} bytes.");
        }

        readDiagnostics = new MDictRecordReadDiagnostics(
            sourceBytesRead,
            decodedBytes,
            result.LongLength,
            ResourceArrayCopyCount: 1,
            allBlocksFromCache);
        return result;
    }

    private byte[] ReadRecordBlock(RecordBlockInfo info, out bool cacheHit)
    {
        var cacheKey = new RecordBlockCacheKey(
            _recordCacheSource,
            info.CompressedOffset,
            info.CompressedSize);
        byte[]? block = null;
        cacheHit = _resourceCache?.TryGetRecordBlock(cacheKey, out block) == true;
        if (!cacheHit)
        {
            lock (_stream)
            {
                cacheHit = _resourceCache?.TryGetRecordBlock(cacheKey, out block) == true;
                if (!cacheHit)
                {
                    ValidateFileRange(info.CompressedOffset, info.CompressedSize, "record block");
                    _stream.Position = info.CompressedOffset;
                    block = DecodeBlock(
                        ReadExactly(CheckedSize(
                            info.CompressedSize,
                            MaximumCompressedBlockBytes,
                            "compressed record block")),
                        info.DecompressedSize);
                    _resourceCache?.StoreRecordBlock(cacheKey, block);
                }
            }
        }

        return block!;
    }

    private int FindRecordBlockIndex(long offset)
    {
        var low = 0;
        var high = _recordBlocks.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var item = _recordBlocks[middle];
            if (offset < item.DecompressedOffset)
            {
                high = middle - 1;
            }
            else if (offset >= item.DecompressedOffset + item.DecompressedSize)
            {
                low = middle + 1;
            }
            else
            {
                return middle;
            }
        }

        return -1;
    }

    private void BuildRecordBlockIndex()
    {
        var originalPosition = _stream.Position;
        try
        {
            _stream.Position = _recordBlockOffset;
            if (Version >= 3.0)
            {
                var count = ReadUInt32BigEndian();
                if (count > MaximumRecordBlockCount)
                {
                    throw new InvalidDataException($"MDict record block count is too large: {count}.");
                }
                _ = ReadNumber();
                long decompressedOffset = 0;
                for (var i = 0; i < count; i++)
                {
                    var decompressedSize = ReadUInt32BigEndian();
                    var compressedSize = ReadUInt32BigEndian();
                    var compressedOffset = _stream.Position;
                    _ = CheckedSize(
                        compressedSize,
                        MaximumCompressedBlockBytes,
                        "compressed record block");
                    _ = CheckedSize(
                        decompressedSize,
                        MaximumDecompressedBlockBytes,
                        "decompressed record block");
                    ValidateFileRange(compressedOffset, compressedSize, "record block");
                    _recordBlocks.Add(new RecordBlockInfo(compressedOffset, compressedSize, decompressedOffset, decompressedSize));
                    decompressedOffset = checked(decompressedOffset + decompressedSize);
                    _stream.Seek(compressedSize, SeekOrigin.Current);
                }
                return;
            }

            var blockCount = ReadNumber();
            if (blockCount < 0 || blockCount > MaximumRecordBlockCount)
            {
                throw new InvalidDataException($"MDict record block count is too large: {blockCount}.");
            }
            _ = ReadNumber();
            var recordBlockInfoSize = ReadNumber();
            _ = ReadNumber();
            long expectedRecordBlockInfoSize = checked(blockCount * _numberWidth * 2L);
            if (recordBlockInfoSize != expectedRecordBlockInfoSize)
            {
                throw new InvalidDataException(
                    $"MDict record block info size is invalid: expected {expectedRecordBlockInfoSize}, got {recordBlockInfoSize}.");
            }

            ValidateFileRange(_stream.Position, recordBlockInfoSize, "record block info");
            var compressedOffsetV2 = checked(_stream.Position + recordBlockInfoSize);
            long decompressedOffsetV2 = 0;
            for (var i = 0; i < blockCount; i++)
            {
                var compressedSize = ReadNumber();
                var decompressedSize = ReadNumber();
                _ = CheckedSize(
                    compressedSize,
                    MaximumCompressedBlockBytes,
                    "compressed record block");
                _ = CheckedSize(
                    decompressedSize,
                    MaximumDecompressedBlockBytes,
                    "decompressed record block");
                ValidateFileRange(compressedOffsetV2, compressedSize, "record block");
                _recordBlocks.Add(new RecordBlockInfo(compressedOffsetV2, compressedSize, decompressedOffsetV2, decompressedSize));
                compressedOffsetV2 = checked(compressedOffsetV2 + compressedSize);
                decompressedOffsetV2 = checked(decompressedOffsetV2 + decompressedSize);
            }
        }
        finally
        {
            _stream.Position = originalPosition;
        }
    }

    private static byte[] DecodeBlock(byte[] block, long decompressedSize)
    {
        if (block.Length < 8)
        {
            throw new InvalidDataException("MDict compressed block header is truncated.");
        }

        var expectedSize = CheckedSize(
            decompressedSize,
            MaximumDecompressedBlockBytes,
            "decompressed block");

        var info = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0, 4));
        var compression = info & 0x0f;
        var encryption = (info >> 4) & 0x0f;
        var encryptionSize = (int)((info >> 8) & 0xff);
        var data = block.AsSpan(8).ToArray();
        if (encryption == 1)
        {
            var key = Ripemd128.Hash(block.AsSpan(4, 4).ToArray());
            var size = Math.Min(encryptionSize, data.Length);
            var decryptedPrefix = FastDecrypt(data.AsSpan(0, size).ToArray(), key);
            Buffer.BlockCopy(decryptedPrefix, 0, data, 0, size);
        }
        else if (encryption != 0)
        {
            throw new NotSupportedException($"Unsupported MDict encryption method: {encryption}.");
        }

        var decoded = compression switch
        {
            0 => data,
            1 => throw new NotSupportedException("LZO-compressed MDict blocks are not supported yet."),
            2 => ZlibDecompress(data, expectedSize),
            _ => throw new NotSupportedException($"Unsupported MDict compression method: {compression}.")
        };

        if (decoded.Length != expectedSize)
        {
            throw new InvalidDataException(
                $"MDict block length mismatch: expected {expectedSize} bytes, decoded {decoded.Length} bytes.");
        }

        return decoded;
    }

    private static byte[] ZlibDecompress(byte[] data)
    {
        const int MaxKeyBlockInfoSize = 64 * 1024 * 1024; // 64 MiB
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Min(MaxKeyBlockInfoSize, 1024 * 1024));
        var buffer = new byte[81920];
        int read;
        while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > MaxKeyBlockInfoSize)
            {
                throw new InvalidDataException(
                    $"Key block info decompressed size exceeds {MaxKeyBlockInfoSize} bytes.");
            }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static byte[] ZlibDecompress(byte[] data, int maximumSize)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(Math.Min(maximumSize, 1024 * 1024));
        var buffer = new byte[81920];
        while (true)
        {
            var read = zlib.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > maximumSize)
            {
                throw new InvalidDataException(
                    $"MDict block exceeds its declared size of {maximumSize} bytes.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static byte[] FastDecrypt(byte[] data, byte[] key)
    {
        var output = data.ToArray();
        byte previous = 0x36;
        for (var i = 0; i < output.Length; i++)
        {
            var current = output[i];
            var value = (byte)(((current >> 4) | (current << 4)) & 0xff);
            value = (byte)(value ^ previous ^ (i & 0xff) ^ key[i % key.Length]);
            previous = current;
            output[i] = value;
        }

        return output;
    }

    private long ReadNumber()
    {
        return _numberWidth == 4 ? ReadUInt32BigEndian() : (long)ReadUInt64BigEndian();
    }

    private uint ReadUInt32BigEndian()
    {
        return BinaryPrimitives.ReadUInt32BigEndian(ReadExactly(4));
    }

    private ulong ReadUInt64BigEndian()
    {
        return BinaryPrimitives.ReadUInt64BigEndian(ReadExactly(8));
    }

    private byte[] ReadExactly(int count)
    {
        var buffer = new byte[count];
        _stream.ReadExactly(buffer);
        return buffer;
    }

    private long ReadInlineNumber(byte[] data, int offset)
    {
        return _numberWidth == 4
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset, 8));
    }

    private static int ReadInlineInteger(byte[] data, int offset, int width)
    {
        return width == 1 ? data[offset] : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
    }

    private int TextByteCount(int characterCount)
    {
        return EncodingName.Equals("UTF-16", StringComparison.OrdinalIgnoreCase) ? characterCount * 2 : characterCount;
    }

    private static string NormalizeEncoding(string encoding)
    {
        return encoding.ToUpperInvariant() switch
        {
            "GBK" or "GB2312" => "GB18030",
            "UTF16" => "UTF-16",
            _ => encoding
        };
    }

    private static int CheckedInt(long value)
    {
        if (value < 0 || value > int.MaxValue)
        {
            throw new InvalidDataException($"MDict block is too large: {value} bytes.");
        }

        return (int)value;
    }

    private static int CheckedSize(long value, int maximum, string label)
    {
        if (value < 0 || value > maximum)
        {
            throw new InvalidDataException(
                $"MDict {label} size is invalid: {value} bytes; maximum is {maximum} bytes.");
        }

        return (int)value;
    }

    private void ValidateFileRange(long offset, long size, string label)
    {
        if (offset < 0 || size < 0 || offset > _stream.Length || size > _stream.Length - offset)
        {
            throw new InvalidDataException(
                $"MDict {label} range is outside the source file.");
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}

public sealed record MDictEntry(string Word, byte[] KeyBytes, long RecordStart, long RecordLength);

internal sealed record RecordBlockInfo(long CompressedOffset, long CompressedSize, long DecompressedOffset, long DecompressedSize);

internal readonly record struct MDictRecordReadDiagnostics(
    long SourceBytesRead,
    long RecordBlockBytesDecoded,
    long ResourceBytesMaterialized,
    int ResourceArrayCopyCount,
    bool RecordBlockCacheHit)
{
    public static MDictRecordReadDiagnostics None { get; } = new(0, 0, 0, 0, false);
}

internal enum MDictKeyLoadMode
{
    Full,
    EntriesOnly,
    CompactResourcesOnly
}
