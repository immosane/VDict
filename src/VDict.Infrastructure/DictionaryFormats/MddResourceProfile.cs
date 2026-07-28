using System.IO;

namespace VDict.Infrastructure.DictionaryFormats;

internal enum DictionaryResourceKind
{
    Unknown,
    Image,
    Audio,
    Style,
    Font,
    Script
}

internal sealed class MddResourceProfile
{
    public int ImageCount { get; set; }
    public int AudioCount { get; set; }
    public int StyleCount { get; set; }
    public int FontCount { get; set; }
    public int ScriptCount { get; set; }
    public int OtherCount { get; set; }

    public int TotalCount => ImageCount + AudioCount + StyleCount + FontCount + ScriptCount + OtherCount;

    public void Observe(string path)
    {
        switch (Classify(path))
        {
            case DictionaryResourceKind.Image: ImageCount++; break;
            case DictionaryResourceKind.Audio: AudioCount++; break;
            case DictionaryResourceKind.Style: StyleCount++; break;
            case DictionaryResourceKind.Font: FontCount++; break;
            case DictionaryResourceKind.Script: ScriptCount++; break;
            default: OtherCount++; break;
        }
    }

    public int Count(DictionaryResourceKind kind) => kind switch
    {
        DictionaryResourceKind.Image => ImageCount,
        DictionaryResourceKind.Audio => AudioCount,
        DictionaryResourceKind.Style => StyleCount,
        DictionaryResourceKind.Font => FontCount,
        DictionaryResourceKind.Script => ScriptCount,
        _ => OtherCount
    };

    public static DictionaryResourceKind Classify(string path)
    {
        return Path.GetExtension(DictionaryStore.NormalizeResourcePath(path)).ToLowerInvariant() switch
        {
            ".avif" or ".bmp" or ".gif" or ".ico" or ".jpeg" or ".jpg" or ".png" or ".svg" or ".webp" => DictionaryResourceKind.Image,
            ".aac" or ".flac" or ".m4a" or ".mp3" or ".ogg" or ".opus" or ".wav" or ".webm" or ".wma" => DictionaryResourceKind.Audio,
            ".css" or ".htm" or ".html" => DictionaryResourceKind.Style,
            ".eot" or ".otf" or ".ttf" or ".woff" or ".woff2" => DictionaryResourceKind.Font,
            ".js" or ".mjs" => DictionaryResourceKind.Script,
            _ => DictionaryResourceKind.Unknown
        };
    }
}
