using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VDict.Infrastructure.DictionaryFormats;

internal sealed class MddResourceIndex
{
    private const int MaximumIndexBytes = 16 * 1024 * 1024;
    private const long GlobalIndexBudget = 64L * 1024L * 1024L;
    // V3 hashes the same case-insensitive identity used by MDictReader's
    // in-memory record dictionaries. V2 hashed the caller's original casing,
    // so an MDX/MDD case mismatch became a false 404 after index persistence.
    private static readonly byte[] Magic = "VDMRI03"u8.ToArray();
    private readonly Dictionary<ResourceHash, (long Offset, long Length)> _records = [];

    public int Count => _records.Count;

    public void Add(string path, long offset, long length)
    {
        var normalized = DictionaryStore.NormalizeResourcePath(path);
        if (normalized.Length == 0) return;
        _records.TryAdd(Hash(normalized), (offset, length));
    }

    public bool TryGet(string path, out (long Offset, long Length) record)
    {
        return _records.TryGetValue(Hash(DictionaryStore.NormalizeResourcePath(path)), out record);
    }

    public static MddResourceIndex? Load(string cacheDirectory, string mddPath)
    {
        var path = IndexPath(cacheDirectory, mddPath);
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic)) return null;
            var info = new FileInfo(mddPath);
            if (reader.ReadInt64() != info.Length || reader.ReadInt64() != info.LastWriteTimeUtc.Ticks) return null;
            var count = reader.ReadInt32();
            if (count < 0 || count > MaximumIndexBytes / 32) return null;
            var index = new MddResourceIndex();
            for (var i = 0; i < count; i++)
            {
                var hash = new ResourceHash(reader.ReadUInt64(), reader.ReadUInt64());
                index._records[hash] = (reader.ReadInt64(), reader.ReadInt64());
            }
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
            return index;
        }
        catch
        {
            return null;
        }
    }

    public void Save(string cacheDirectory, string mddPath)
    {
        var estimatedSize = Magic.Length + 20L + (Count * 32L);
        if (estimatedSize > MaximumIndexBytes)
        {
            try { File.Delete(IndexPath(cacheDirectory, mddPath)); } catch { }
            return;
        }
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var path = IndexPath(cacheDirectory, mddPath);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var info = new FileInfo(mddPath);
            using (var stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(info.Length);
                writer.Write(info.LastWriteTimeUtc.Ticks);
                writer.Write(Count);
                foreach (var item in _records)
                {
                    writer.Write(item.Key.First);
                    writer.Write(item.Key.Second);
                    writer.Write(item.Value.Offset);
                    writer.Write(item.Value.Length);
                }
            }
            File.Move(temporary, path, overwrite: true);
            TrimGlobalBudget(cacheDirectory);
        }
        catch
        {
            // The in-memory index remains usable when persistence is unavailable.
        }
        finally
        {
            try
            {
                if (temporary is not null && File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch
            {
            }
        }
    }

    private static string IndexPath(string cacheDirectory, string mddPath)
    {
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(mddPath))))[..20];
        return Path.Combine(cacheDirectory, $"vdict-mdd-{digest}.resource-index");
    }

    private static void TrimGlobalBudget(string cacheDirectory)
    {
        var files = new DirectoryInfo(cacheDirectory)
            .EnumerateFiles("vdict-mdd-*.resource-index")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();
        long retained = 0;
        foreach (var file in files)
        {
            retained += file.Length;
            if (retained <= GlobalIndexBudget) continue;
            try { file.Delete(); } catch { }
        }
    }

    private static ResourceHash Hash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value.ToUpperInvariant()));
        return new ResourceHash(
            BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(0, 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(digest.AsSpan(8, 8)));
    }

    private readonly record struct ResourceHash(ulong First, ulong Second);
}
