using System.Collections.Immutable;
using VDict.Core.Models;
using VDict.Infrastructure.DictionaryFormats;

namespace VDict.Infrastructure.Catalog;

public sealed record DictionaryFileInspection(
    string FullMdxPath,
    string SuggestedName,
    ImmutableArray<string> MddPaths,
    ImmutableArray<string> CssPaths)
{
    public ImmutableArray<string> AutoScriptPaths { get; init; } = [];
}

public sealed class DictionaryFileInspector
{
    public DictionaryFileInspection Inspect(string mdxPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mdxPath);
        string fullPath = Path.GetFullPath(mdxPath);
        if (!File.Exists(fullPath) ||
            !Path.GetExtension(fullPath).Equals(".mdx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("只能添加存在的 MDX 文件。");
        }

        string directory = Path.GetDirectoryName(fullPath) ??
            throw new InvalidDataException("无法确定 MDX 所在目录。");
        string stem = Path.GetFileNameWithoutExtension(fullPath);
        string suggestedName = stem;
        try
        {
            string? title = MDictHeaderReader.Read(fullPath).GetValueOrDefault("Title")?.Trim();
            if (!string.IsNullOrWhiteSpace(title))
            {
                suggestedName = title;
            }
        }
        catch (Exception exception)
            when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
        }

        string? sameStemScript = DictionaryScriptDiscovery.FindSameStemScript(fullPath);
        return new DictionaryFileInspection(
            fullPath,
            suggestedName,
            Enumerate(directory, stem + "*.mdd"),
            Enumerate(directory, "*.css"))
        {
            AutoScriptPaths = sameStemScript is null ? [] : [sameStemScript]
        };
    }

    public DictionaryDefinition CreateDefault(string mdxPath)
    {
        DictionaryFileInspection inspection = Inspect(mdxPath);
        return new DictionaryDefinition(
            new DictionaryId(Guid.NewGuid().ToString("N")),
            inspection.SuggestedName,
            null,
            inspection.FullMdxPath,
            true,
            true,
            inspection.MddPaths,
            inspection.CssPaths,
            DictionaryAppearance.NewDictionaryDefault);
    }

    private static ImmutableArray<string> Enumerate(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
