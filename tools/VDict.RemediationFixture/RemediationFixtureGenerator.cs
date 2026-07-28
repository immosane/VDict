using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VDict.RemediationFixture;

public static class RemediationFixtureGenerator
{
    public const string FixtureId = "vdict-remediation-0a";
    public const string GeneratorVersion = "0a.1";
    internal const string MarkerFileName = ".vdict-remediation-fixture";
    internal const string ManifestFileName = "fixture-manifest.json";
    internal const string MdxFileName = "vdict-remediation.mdx";
    internal const string MddFileName = "vdict-remediation.mdd";

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static FixtureValidationReport GenerateAndVerify(string outputDirectory)
    {
        string output = Path.GetFullPath(outputDirectory);
        EnsureSafeOutput(output);
        string parent = Directory.GetParent(output)?.FullName
            ?? throw new InvalidOperationException("Fixture output must have a parent directory.");
        Directory.CreateDirectory(parent);
        string staging = output + ".staging-" + Guid.NewGuid().ToString("N");
        string backup = output + ".previous-" + Guid.NewGuid().ToString("N");

        try
        {
            Directory.CreateDirectory(staging);
            WriteFixture(staging);
            FixtureValidationReport report = RemediationFixtureValidator.Validate(staging);

            bool hadPrevious = Directory.Exists(output);
            if (hadPrevious)
            {
                EnsureOwnedFixtureDirectory(output);
                Directory.Move(output, backup);
            }

            try
            {
                Directory.Move(staging, output);
                if (hadPrevious)
                {
                    Directory.Delete(backup, recursive: true);
                }
            }
            catch
            {
                if (!Directory.Exists(output) && Directory.Exists(backup))
                {
                    Directory.Move(backup, output);
                }

                throw;
            }

            return report;
        }
        catch
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            throw;
        }
    }

    private static void WriteFixture(string directory)
    {
        IReadOnlyList<EntryDefinition> entries = BuildEntries();
        IReadOnlyList<ResourceDefinition> resources = BuildResources();
        foreach (ResourceDefinition resource in resources.Where(resource => resource.Source == "local"))
        {
            string path = Path.Combine(directory, resource.LogicalPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, resource.Body);
        }

        ImmutableArray<(string Key, byte[] Record)> mdxRecords = entries
            .Select(entry => (entry.Query, Utf8.GetBytes(entry.Body)))
            .ToImmutableArray();
        ImmutableArray<(string Key, byte[] Record)> mddRecords = resources
            .Where(resource => resource.Source == "mdd")
            .Select(resource => ("\\" + resource.LogicalPath.Replace('/', '\\'), resource.Body))
            .ToImmutableArray();
        MDictFixtureWriter.Write(
            Path.Combine(directory, MdxFileName),
            mdxRecords,
            utf16Keys: false,
            title: "VDict Remediation Fixture");
        MDictFixtureWriter.Write(
            Path.Combine(directory, MddFileName),
            mddRecords,
            utf16Keys: true,
            title: "VDict Remediation Resources");

        List<FixtureEntryExpectation> entryExpectations = entries
            .Select((entry, index) => new FixtureEntryExpectation(
                entry.Query,
                index,
                entry.Marker,
                Sha256(Utf8.GetBytes(entry.Body)),
                entry.Features,
                entry.Expected))
            .ToList();
        List<FixtureResourceExpectation> resourceExpectations = resources
            .Select(resource => new FixtureResourceExpectation(
                resource.LogicalPath,
                resource.Source,
                resource.Mime,
                resource.Body.LongLength,
                Sha256(resource.Body)))
            .ToList();

        List<FixtureFileHash> files = Directory
            .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Select(path => CreateFileHash(directory, path))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
        string semanticHash = FixtureSemantics.Compute(entryExpectations, resourceExpectations);
        FixtureManifest manifest = new(
            FixtureId,
            GeneratorVersion,
            "MDict v1.2, uncompressed, fixed UTF-8/UTF-16 encoding and ordering",
            semanticHash,
            entryExpectations,
            resourceExpectations,
            files);
        string json = JsonSerializer.Serialize(manifest, JsonOptions) + "\n";
        File.WriteAllText(Path.Combine(directory, ManifestFileName), json, Utf8);
        File.WriteAllText(Path.Combine(directory, MarkerFileName), FixtureId + "\n", Utf8);
    }

    private static IReadOnlyList<EntryDefinition> BuildEntries()
    {
        List<EntryDefinition> entries =
        [
            Entry(
                "fixture-audio",
                "fixture-audio-marker",
                """
                <article id="fixture-audio-marker">
                  <a id="sound-scheme" href="sound://mdd-tone.wav">sound scheme</a>
                  <button id="data-audio" data-audio-src="assets/local-tone.wav">data audio</button>
                  <a id="plain-audio" href="mdd-tone.wav">plain audio</a>
                  <audio id="audio-element" controls src="assets/local-tone.wav"><source src="mdd-tone.wav" type="audio/wav"></audio>
                  <button id="script-audio">script audio</button>
                  <script>document.querySelector('#script-audio').addEventListener('click',()=>new Audio('mdd-tone.wav').play());</script>
                </article>
                """,
                ["sound-scheme", "data-audio-src", "audio-element", "script-audio", "local-audio", "mdd-audio"],
                ("cold_play", "playing"), ("warm_play", "playing"), ("pause_resume", "currentTime-preserved")),
            Entry(
                "fixture-basic",
                "fixture-basic-marker",
                """
                <article id="fixture-basic-marker">
                  <h1>Basic fixture</h1>
                  <a href="#fixture-basic-detail">fragment</a>
                  <a href="entry://fixture-multi-entry">internal entry</a>
                  <p id="fixture-basic-detail">basic detail</p>
                </article>
                """,
                ["basic-html", "fragment", "internal-link", "missing-query"],
                ("fragment", "fixture-basic-detail"), ("internal_link", "fixture-multi-entry"),
                ("missing_query", "fixture-missing")),
            Entry(
                "fixture-css",
                "fixture-css-marker",
                """
                <link rel="stylesheet" href="styles/fixture.css">
                <article id="fixture-css-marker" class="fixture-css-card">
                  <button class="fixture-css-toggle">toggle</button>
                  <div class="fixture-css-collapsible">long scrolling content</div>
                </article>
                """,
                ["css-variables", "css-import", "css-url", "pseudo-element", "transparent-color", "hover-active", "collapse", "long-scroll", "complex-selector"],
                ("stylesheet", "styles/fixture.css"), ("pseudo_content", "fixture-css-pseudo")),
            Entry(
                "fixture-image",
                "fixture-image-marker",
                """
                <article id="fixture-image-marker">
                  <img id="local-png" src="assets/local.png"><img src="assets/local.jpg">
                  <img src="assets/local.gif"><img src="assets/local.svg"><img src="assets/local.webp">
                  <img id="mdd-png" src="mdd-image.png"><img src="mdd-image.jpg">
                  <img src="mdd-image.gif"><img src="mdd-image.svg"><img src="mdd-image.webp">
                  <a id="zoom-link" href="zoom://mdd-image.png">zoom image</a>
                  <button id="custom-viewer">custom viewer</button>
                  <script>
                    const image=document.createElement('img'); image.id='dynamic-image'; image.src='mdd-image.png'; document.currentScript.before(image);
                    document.querySelector('#custom-viewer').addEventListener('click',()=>document.body.dataset.viewer='open');
                  </script>
                </article>
                """,
                ["png", "jpeg", "gif", "svg", "webp", "local-image", "mdd-image", "dynamic-image", "single-click", "double-click", "custom-viewer", "zoom-scheme", "escape-focus"],
                ("image_count", "11"), ("viewer", "interactive"), ("dynamic_image", "dynamic-image")),
            Entry(
                "fixture-isolation",
                "fixture-isolation-marker",
                """
                <article id="fixture-isolation-marker">
                  <script>
                    window.fixtureIsolation={top:false,sibling:false,storage:false,message:false};
                    try{window.fixtureIsolation.top=top.document!==document;}catch{}
                    try{window.fixtureIsolation.sibling=parent.frames.length>1;}catch{}
                    localStorage.setItem('fixture-isolation','attempt');
                    parent.postMessage({type:'fixture-isolation-side-effect'},'*');
                  </script>
                </article>
                """,
                ["staging", "retired", "sibling-dom", "shared-storage", "media", "message", "external-link", "download"],
                ("staging_side_effects", "blocked"), ("retired_interaction", "blocked")),
            Entry(
                "fixture-malformed",
                "fixture-malformed-marker",
                """
                <article id=fixture-malformed-marker><style>.broken{color:red</style>
                <script>window.fixtureMalformed='survived'</script></div>
                <img src="missing-image.png"><audio src="wrong-mime.bin"></audio>
                """,
                ["malformed-html", "malformed-style", "malformed-script-wrapper", "missing-resource", "wrong-mime", "delayed-frame-failure"],
                ("allowed_error", "missing-resource"), ("document", "must-remain-queryable")),
            Entry(
                "fixture-multi-entry",
                "fixture-multi-entry-1",
                """<article id="fixture-multi-entry-1"><a href="#first">first entry</a><p id="first">first</p></article>""",
                ["multi-entry", "frame-order", "fragment-anchor", "candidate-order"],
                ("entry_index", "1"), ("entry_count", "3")),
            Entry(
                "fixture-multi-entry",
                "fixture-multi-entry-2",
                """<article id="fixture-multi-entry-2"><a href="#second">second entry</a><p id="second">second</p></article>""",
                ["multi-entry", "frame-order", "fragment-anchor", "candidate-order"],
                ("entry_index", "2"), ("entry_count", "3")),
            Entry(
                "fixture-multi-entry",
                "fixture-multi-entry-3",
                """<article id="fixture-multi-entry-3"><a href="#third">third entry</a><p id="third">third</p></article>""",
                ["multi-entry", "frame-order", "fragment-anchor", "candidate-order"],
                ("entry_index", "3"), ("entry_count", "3")),
            Entry(
                "fixture-perf-large",
                "fixture-perf-large-marker",
                PerformanceBody("large", 2048),
                ["perf", "large", "fixed-html", "fixed-css", "fixed-resources"],
                ("paragraphs", "2048"), ("frames", "1")),
            Entry(
                "fixture-perf-medium",
                "fixture-perf-medium-marker",
                PerformanceBody("medium", 256),
                ["perf", "medium", "fixed-html", "fixed-css", "fixed-resources"],
                ("paragraphs", "256"), ("frames", "1")),
            Entry(
                "fixture-perf-small",
                "fixture-perf-small-marker",
                PerformanceBody("small", 16),
                ["perf", "small", "fixed-html", "fixed-css", "fixed-resources"],
                ("paragraphs", "16"), ("frames", "1")),
            Entry(
                "fixture-redirect",
                "@@@LINK=fixture-basic",
                "@@@LINK=fixture-basic",
                ["redirect"],
                ("target", "fixture-basic")),
            Entry(
                "fixture-script-legacy",
                "fixture-script-legacy-marker",
                """
                <article id="fixture-script-legacy-marker">
                  <script>
                    window.fixtureLegacy={evalResult:eval('40+2'),functionResult:Function('return 42')()};
                  </script>
                </article>
                """,
                ["eval", "function-constructor", "legacy-only"],
                ("legacy", "allowed"), ("standard", "blocked")),
            Entry(
                "fixture-script-standard",
                "fixture-script-standard-marker",
                """
                <article id="fixture-script-standard-marker">
                  <button id="standard-event">event</button>
                  <script>window.fixtureInline=true;document.querySelector('#standard-event').addEventListener('click',()=>document.body.dataset.standard='clicked');</script>
                  <script src="scripts/standard.js"></script>
                  <script type="module" src="scripts/module.js"></script>
                </article>
                """,
                ["inline-script", "external-script", "module-script", "local-fetch", "local-storage", "dynamic-dom", "event-registration"],
                ("standard", "allowed"), ("network", "not-required")),
            Entry(
                "fixture-security",
                "fixture-security-marker",
                """
                <article id="fixture-security-marker">
                  <a href="http://example.test">http</a><a href="https://example.test" target="_blank">https blank</a>
                  <a href="file:///C:/secret.txt">file</a><a href="ftp://example.test/file">ftp</a>
                  <a href="ws://example.test">ws</a><a href="wss://example.test">wss</a>
                  <a href="https://example.test/file" download>download</a>
                  <script>
                    parent.postMessage({type:'external-link',userActivation:true,url:'https://example.test'},'*');
                    setTimeout(()=>parent.postMessage({type:'download',userActivation:true,url:'https://example.test/file'},'*'),500);
                  </script>
                </article>
                """,
                ["http", "https", "file", "ftp", "ws", "wss", "target-blank", "download", "forged-user-activation", "delayed-replay", "cross-session-message"],
                ("network", "blocked"), ("external_link", "host-confirmation-required"),
                ("download", "host-confirmation-required"), ("replay", "blocked"))
        ];

        return entries;
    }

    private static IReadOnlyList<ResourceDefinition> BuildResources()
    {
        string css =
            """
            @import url("nested.css");
            :root{--fixture-accent:#287a45;--fixture-transparent:rgba(40,122,69,.35)}
            .fixture-css-card{color:var(--fixture-accent);background:var(--fixture-transparent) url("../assets/local.png")}
            .fixture-css-card::before{content:"fixture-css-pseudo"}
            .fixture-css-card:hover{outline:2px solid var(--fixture-accent)}
            .fixture-css-toggle:active+.fixture-css-collapsible{display:block}
            .fixture-css-collapsible{min-height:1800px}
            body:is(.dictionary-document-shell,.fixture-shell) .fixture-css-card:not([hidden]){border:1px solid currentColor}
            """;
        string nestedCss = """@font-face{font-family:FixtureSans;src:url("../assets/local.woff2")}""";
        string standardScript =
            """
            window.fixtureExternal=true;
            const dynamic=document.createElement('p');dynamic.id='fixture-dynamic-dom';dynamic.textContent='dynamic';document.currentScript.before(dynamic);
            localStorage.setItem('fixture-standard','stored');
            fetch('../data/fixture.json').then(r=>r.json()).then(v=>window.fixtureFetch=v.mode);
            """;
        string moduleScript =
            """import {value} from './module-dependency.js'; window.fixtureModule=value+1;""";

        return
        [
            Resource("assets/local.png", "local", "image/png", FixtureAssets.Png),
            Resource("assets/local.jpg", "local", "image/jpeg", FixtureAssets.Jpeg),
            Resource("assets/local.gif", "local", "image/gif", FixtureAssets.Gif),
            Resource("assets/local.svg", "local", "image/svg+xml", FixtureAssets.Svg),
            Resource("assets/local.webp", "local", "image/webp", FixtureAssets.WebP),
            Resource("assets/local-tone.wav", "local", "audio/wav", FixtureAssets.Wave),
            Resource("assets/local.woff2", "local", "font/woff2", [0x77, 0x4f, 0x46, 0x32, 0, 0, 0, 0]),
            Resource("data/fixture.json", "local", "application/json", Utf8.GetBytes("""{"mode":"offline"}""")),
            Resource("scripts/module-dependency.js", "local", "text/javascript", Utf8.GetBytes("export const value=41;\n")),
            Resource("scripts/module.js", "local", "text/javascript", Utf8.GetBytes(moduleScript + "\n")),
            Resource("scripts/standard.js", "local", "text/javascript", Utf8.GetBytes(standardScript + "\n")),
            Resource("styles/fixture.css", "local", "text/css", Utf8.GetBytes(css + "\n")),
            Resource("styles/nested.css", "local", "text/css", Utf8.GetBytes(nestedCss + "\n")),
            Resource("mdd-image.png", "mdd", "image/png", FixtureAssets.Png),
            Resource("mdd-image.jpg", "mdd", "image/jpeg", FixtureAssets.Jpeg),
            Resource("mdd-image.gif", "mdd", "image/gif", FixtureAssets.Gif),
            Resource("mdd-image.svg", "mdd", "image/svg+xml", FixtureAssets.Svg),
            Resource("mdd-image.webp", "mdd", "image/webp", FixtureAssets.WebP),
            Resource("mdd-tone.wav", "mdd", "audio/wav", FixtureAssets.Wave),
            Resource("mdd-standard.js", "mdd", "text/javascript", Utf8.GetBytes("window.fixtureMddScript=true;\n")),
            Resource("mdd-style.css", "mdd", "text/css", Utf8.GetBytes(".mdd-style{color:#287a45}\n")),
            Resource("wrong-mime.bin", "mdd", "application/octet-stream", FixtureAssets.Wave)
        ];
    }

    private static EntryDefinition Entry(
        string query,
        string marker,
        string body,
        IReadOnlyList<string> features,
        params (string Key, string Value)[] expected)
    {
        Dictionary<string, string> expectation = expected.ToDictionary(
            item => item.Key,
            item => item.Value,
            StringComparer.Ordinal);
        return new EntryDefinition(
            query,
            marker,
            body.Replace("\r\n", "\n", StringComparison.Ordinal),
            features,
            expectation);
    }

    private static ResourceDefinition Resource(
        string logicalPath,
        string source,
        string mime,
        byte[] body) =>
        new(logicalPath, source, mime, body);

    private static string PerformanceBody(string size, int paragraphs)
    {
        StringBuilder body = new();
        body.Append("<article id=\"fixture-perf-").Append(size).Append("-marker\"><style>");
        body.Append(".perf-row:nth-child(3n){color:#287a45}.perf-row::before{content:attr(data-index)}</style>");
        for (int index = 0; index < paragraphs; index++)
        {
            body.Append("<p class=\"perf-row\" data-index=\"").Append(index)
                .Append("\">deterministic performance payload ")
                .Append(index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))
                .Append("</p>");
        }

        return body.Append("</article>").ToString();
    }

    private static FixtureFileHash CreateFileHash(string root, string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return new FixtureFileHash(
            Path.GetRelativePath(root, path).Replace('\\', '/'),
            bytes.LongLength,
            Sha256(bytes));
    }

    internal static string Sha256(byte[] bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void EnsureSafeOutput(string output)
    {
        string root = Path.GetPathRoot(output)
            ?? throw new InvalidOperationException("Fixture output must be an absolute filesystem path.");
        if (string.Equals(
                output.TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Fixture output cannot be a filesystem root.");
        }

        if (File.Exists(output))
        {
            throw new InvalidOperationException($"Fixture output is an existing file: {output}");
        }
    }

    private static void EnsureOwnedFixtureDirectory(string output)
    {
        string marker = Path.Combine(output, MarkerFileName);
        if (!File.Exists(marker) ||
            !string.Equals(File.ReadAllText(marker).Trim(), FixtureId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Refusing to replace unowned fixture directory '{output}'. " +
                $"Expected marker {MarkerFileName}={FixtureId}.");
        }
    }

    private sealed record EntryDefinition(
        string Query,
        string Marker,
        string Body,
        IReadOnlyList<string> Features,
        IReadOnlyDictionary<string, string> Expected);

    private sealed record ResourceDefinition(
        string LogicalPath,
        string Source,
        string Mime,
        byte[] Body);
}

internal static class FixtureSemantics
{
    public static string Compute(
        IReadOnlyList<FixtureEntryExpectation> entries,
        IReadOnlyList<FixtureResourceExpectation> resources)
    {
        StringBuilder canonical = new();
        foreach (FixtureEntryExpectation entry in entries)
        {
            canonical.Append("E|").Append(entry.Ordinal).Append('|').Append(entry.Query)
                .Append('|').Append(entry.Marker).Append('|').Append(entry.BodySha256)
                .Append('|').AppendJoin(',', entry.Features).Append('|');
            foreach (KeyValuePair<string, string> pair in entry.Expected.OrderBy(
                         pair => pair.Key,
                         StringComparer.Ordinal))
            {
                canonical.Append(pair.Key).Append('=').Append(pair.Value).Append(';');
            }

            canonical.Append('\n');
        }

        foreach (FixtureResourceExpectation resource in resources)
        {
            canonical.Append("R|").Append(resource.LogicalPath).Append('|')
                .Append(resource.Source).Append('|').Append(resource.Mime).Append('|')
                .Append(resource.Length).Append('|').Append(resource.Sha256).Append('\n');
        }

        return RemediationFixtureGenerator.Sha256(Encoding.UTF8.GetBytes(canonical.ToString()));
    }
}
