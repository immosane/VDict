using System.Text;
using System.Text.Json;

namespace VDict.Infrastructure.DictionaryFormats;

internal sealed class MddProfileManifest
{
    private readonly string _path;
    private readonly Dictionary<string, MddProfileEntry> _entries;

    public MddProfileManifest(string cacheFile)
    {
        _path = cacheFile + ".mdd-profiles.json";
        _entries = Load(_path);
    }

    public MddResourceProfile? ProfileFor(string mddPath)
    {
        try
        {
            string fullPath = Path.GetFullPath(mddPath);
            FileInfo info = new(fullPath);
            return _entries.TryGetValue(fullPath, out MddProfileEntry? entry) &&
                   entry.Length == info.Length &&
                   entry.LastWriteUtcTicks == info.LastWriteTimeUtc.Ticks
                ? entry.Profile
                : null;
        }
        catch
        {
            return null;
        }
    }

    public void Update(string mddPath, MddResourceProfile profile)
    {
        try
        {
            string fullPath = Path.GetFullPath(mddPath);
            FileInfo info = new(fullPath);
            _entries[fullPath] = new(
                info.Length,
                info.LastWriteTimeUtc.Ticks,
                profile);
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(_entries),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporary, _path, overwrite: true);
        }
        catch
        {
            // Profiles only optimize routing; resource lookup remains authoritative.
        }
    }

    private static Dictionary<string, MddProfileEntry> Load(string path)
    {
        try
        {
            Dictionary<string, MddProfileEntry>? loaded = File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, MddProfileEntry>>(
                    File.ReadAllText(path, Encoding.UTF8))
                : null;
            return loaded is null
                ? new(StringComparer.OrdinalIgnoreCase)
                : new(loaded, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed record MddProfileEntry(
        long Length,
        long LastWriteUtcTicks,
        MddResourceProfile Profile);
}
