using System.Text;
using System.Text.Json;
using VDict.Infrastructure.DictionaryFormats;

namespace VDict.RemediationFixture;

public static class RemediationFixtureValidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public static FixtureValidationReport Validate(string outputDirectory)
    {
        string root = Path.GetFullPath(outputDirectory);
        string manifestPath = Path.Combine(root, RemediationFixtureGenerator.ManifestFileName);
        FixtureManifest manifest = JsonSerializer.Deserialize<FixtureManifest>(
            File.ReadAllText(manifestPath, Encoding.UTF8),
            JsonOptions) ?? throw new InvalidDataException("Fixture manifest is empty.");
        Require(
            manifest.FixtureId == RemediationFixtureGenerator.FixtureId,
            $"Unexpected fixture id '{manifest.FixtureId}'.");
        Require(
            manifest.GeneratorVersion == RemediationFixtureGenerator.GeneratorVersion,
            $"Unexpected generator version '{manifest.GeneratorVersion}'.");
        Require(
            manifest.SemanticSha256 == FixtureSemantics.Compute(manifest.Entries, manifest.Resources),
            "Manifest semantic SHA-256 does not match its expectations.");

        foreach (FixtureFileHash file in manifest.Files)
        {
            string path = ResolveWithin(root, file.Path);
            Require(File.Exists(path), $"Fixture file is missing: {file.Path}");
            byte[] bytes = File.ReadAllBytes(path);
            Require(bytes.LongLength == file.Length, $"Fixture file length mismatch: {file.Path}");
            Require(
                RemediationFixtureGenerator.Sha256(bytes) == file.Sha256,
                $"Fixture file SHA-256 mismatch: {file.Path}");
        }

        using MDictReader mdx = new(Path.Combine(root, RemediationFixtureGenerator.MdxFileName), loadKeys: true);
        Require(
            mdx.Entries.Count == manifest.Entries.Count,
            $"MDX entry count mismatch: expected {manifest.Entries.Count}, got {mdx.Entries.Count}.");
        for (int index = 0; index < manifest.Entries.Count; index++)
        {
            FixtureEntryExpectation expected = manifest.Entries[index];
            MDictEntry actual = mdx.Entries[index];
            Require(actual.Word == expected.Query, $"MDX entry {index} key mismatch.");
            string body = mdx.ReadTextRecord(actual.RecordStart, actual.RecordLength);
            Require(
                body.Contains(expected.Marker, StringComparison.Ordinal),
                $"MDX entry '{expected.Query}' does not contain marker '{expected.Marker}'.");
            Require(
                RemediationFixtureGenerator.Sha256(Encoding.UTF8.GetBytes(body)) == expected.BodySha256,
                $"MDX entry '{expected.Query}' body SHA-256 mismatch.");
        }

        Require(
            mdx.Entries.All(entry => entry.Word != "fixture-missing"),
            "The intentionally missing fixture query was unexpectedly generated.");
        FixtureEntryExpectation redirect = manifest.Entries.Single(entry => entry.Query == "fixture-redirect");
        Require(
            mdx.ReadTextRecord(
                mdx.Entries[redirect.Ordinal].RecordStart,
                mdx.Entries[redirect.Ordinal].RecordLength) == "@@@LINK=fixture-basic",
            "Fixture redirect target mismatch.");
        Require(
            manifest.Entries.Count(entry => entry.Query == "fixture-multi-entry") == 3,
            "Fixture must contain exactly three ordered multi-entry records.");

        using MDictReader mdd = new(Path.Combine(root, RemediationFixtureGenerator.MddFileName), loadKeys: true);
        foreach (FixtureResourceExpectation resource in manifest.Resources)
        {
            byte[] body = resource.Source switch
            {
                "local" => File.ReadAllBytes(ResolveWithin(root, resource.LogicalPath)),
                "mdd" => mdd.ReadResource(resource.LogicalPath)
                    ?? throw new InvalidDataException($"MDD resource is missing: {resource.LogicalPath}"),
                _ => throw new InvalidDataException(
                    $"Unknown fixture resource source '{resource.Source}' for {resource.LogicalPath}.")
            };
            Require(body.LongLength == resource.Length, $"Resource length mismatch: {resource.LogicalPath}");
            Require(
                RemediationFixtureGenerator.Sha256(body) == resource.Sha256,
                $"Resource SHA-256 mismatch: {resource.LogicalPath}");
        }

        ValidateMatrix(manifest);
        return new FixtureValidationReport(
            manifest.GeneratorVersion,
            manifest.Entries.Count,
            manifest.Resources.Count,
            manifest.SemanticSha256);
    }

    private static void ValidateMatrix(FixtureManifest manifest)
    {
        string[] requiredQueries =
        [
            "fixture-basic",
            "fixture-multi-entry",
            "fixture-css",
            "fixture-script-standard",
            "fixture-script-legacy",
            "fixture-image",
            "fixture-audio",
            "fixture-security",
            "fixture-isolation",
            "fixture-malformed",
            "fixture-perf-small",
            "fixture-perf-medium",
            "fixture-perf-large"
        ];
        foreach (string query in requiredQueries)
        {
            Require(
                manifest.Entries.Any(entry => entry.Query == query),
                $"Required fixture matrix query is missing: {query}");
        }

        string[] machineExpectedKeys =
        [
            "standard",
            "legacy",
            "network",
            "external_link",
            "download",
            "cold_play",
            "viewer",
            "allowed_error"
        ];
        HashSet<string> expectedKeys = manifest.Entries
            .SelectMany(entry => entry.Expected.Keys)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string key in machineExpectedKeys)
        {
            Require(expectedKeys.Contains(key), $"Machine expectation is missing: {key}");
        }

        foreach (string extension in new[] { ".png", ".jpg", ".gif", ".svg", ".webp", ".wav" })
        {
            Require(
                manifest.Resources.Any(resource =>
                    resource.Source == "local" &&
                    resource.LogicalPath.EndsWith(extension, StringComparison.Ordinal)),
                $"Local fixture resource type is missing: {extension}");
            Require(
                manifest.Resources.Any(resource =>
                    resource.Source == "mdd" &&
                    resource.LogicalPath.EndsWith(extension, StringComparison.Ordinal)),
                $"MDD fixture resource type is missing: {extension}");
        }
    }

    private static string ResolveWithin(string root, string relativePath)
    {
        string path = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Fixture path escapes the output directory: {relativePath}");
        }

        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidDataException(message);
        }
    }
}
