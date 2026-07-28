using System.Text.Json.Serialization;

namespace VDict.RemediationFixture;

public sealed record FixtureManifest(
    [property: JsonPropertyName("fixture_id")] string FixtureId,
    [property: JsonPropertyName("generator_version")] string GeneratorVersion,
    [property: JsonPropertyName("container_format")] string ContainerFormat,
    [property: JsonPropertyName("semantic_sha256")] string SemanticSha256,
    [property: JsonPropertyName("entries")] IReadOnlyList<FixtureEntryExpectation> Entries,
    [property: JsonPropertyName("resources")] IReadOnlyList<FixtureResourceExpectation> Resources,
    [property: JsonPropertyName("files")] IReadOnlyList<FixtureFileHash> Files);

public sealed record FixtureEntryExpectation(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("marker")] string Marker,
    [property: JsonPropertyName("body_sha256")] string BodySha256,
    [property: JsonPropertyName("features")] IReadOnlyList<string> Features,
    [property: JsonPropertyName("expected")] IReadOnlyDictionary<string, string> Expected);

public sealed record FixtureResourceExpectation(
    [property: JsonPropertyName("logical_path")] string LogicalPath,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("mime")] string Mime,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record FixtureFileHash(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("length")] long Length,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record FixtureValidationReport(
    string GeneratorVersion,
    int EntryCount,
    int ResourceCount,
    string SemanticSha256);
