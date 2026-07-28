using System.Collections.Immutable;
using VDict.Core.Models;
using VDict.Core.Policies;
using VDict.Presentation.Services;
using VDict.Presentation.Tests.TestDoubles;

namespace VDict.Presentation.Tests.Services;

public sealed class ReaderWebResourceAdapterTests
{
    [Fact]
    public async Task Capability_AuthorizesWithoutOriginAndRejectsCrossDictionaryHost()
    {
        FakeCatalog catalog = ResourceCatalog(7, "text/plain", "ok"u8.ToArray());
        ReaderDocument document = Document(7, "dict-a", "dict-b");
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument(document);
        ReaderFrame first = document.Frames[0];
        string valid = DictionaryNetworkPolicy.ResourceUriForDictionary(
            first.DictionaryId.Value,
            7,
            first.ResourceCapability,
            "data.txt");

        ReaderResourceResponse success = await adapter.ResolveAsync(new(
            valid,
            null,
            null,
            null));
        string forged = valid.Replace(
            DictionaryNetworkPolicy.FrameHostForDictionary("dict-a"),
            DictionaryNetworkPolicy.FrameHostForDictionary("dict-b"),
            StringComparison.Ordinal);
        ReaderResourceResponse rejected = await adapter.ResolveAsync(new(
            forged,
            null,
            null,
            null));

        Assert.Equal(200, success.StatusCode);
        Assert.Equal(403, rejected.StatusCode);
    }

    [Fact]
    public async Task Capability_RejectsMismatchedOriginWithForbiddenResponse()
    {
        FakeCatalog catalog = ResourceCatalog(7, "text/plain", "ok"u8.ToArray());
        ReaderDocument document = Document(7, "dict");
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument(document);
        ReaderFrame frame = Assert.Single(document.Frames);
        string uri = DictionaryNetworkPolicy.ResourceUriForDictionary(
            "dict",
            7,
            frame.ResourceCapability,
            "data.txt");

        ReaderResourceResponse response = await adapter.ResolveAsync(new(
            uri,
            DictionaryNetworkPolicy.FrameOriginForDictionary("other"),
            null,
            null));

        Assert.Equal(403, response.StatusCode);
        Assert.Contains(
            "origin",
            response.Diagnostic ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Capability_RejectsMismatchedRefererWithForbiddenResponse()
    {
        FakeCatalog catalog = ResourceCatalog(7, "text/plain", "ok"u8.ToArray());
        ReaderDocument document = Document(7, "dict");
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument(document);
        ReaderFrame frame = Assert.Single(document.Frames);
        string uri = DictionaryNetworkPolicy.ResourceUriForDictionary(
            "dict",
            7,
            frame.ResourceCapability,
            "data.txt");

        ReaderResourceResponse response = await adapter.ResolveAsync(new(
            uri,
            null,
            DictionaryNetworkPolicy.FrameOriginForDictionary("other") + "/render/page.html",
            null));

        Assert.Equal(403, response.StatusCode);
        Assert.Contains(
            "referer",
            response.Diagnostic ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenerationChange_InvalidatesOldResourceCapability()
    {
        FakeCatalog catalog = ResourceCatalog(3, "text/plain", "ok"u8.ToArray());
        ReaderDocument document = Document(3, "dict");
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument(document);
        ReaderFrame frame = Assert.Single(document.Frames);
        string uri = DictionaryNetworkPolicy.ResourceUriForDictionary(
            "dict",
            3,
            frame.ResourceCapability,
            "data.txt");
        catalog.Generation = 4;

        ReaderResourceResponse response = await adapter.ResolveAsync(new(
            uri,
            null,
            null,
            null));

        Assert.Equal(410, response.StatusCode);
        Assert.Equal(0, catalog.ResourceRequests);
    }

    [Fact]
    public async Task Head_ReturnsMetadataWithoutBody()
    {
        FakeCatalog catalog = ResourceCatalog(5, "audio/mpeg", new byte[120]);
        ReaderDocument document = Document(5, "dict");
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument(document);
        ReaderFrame frame = Assert.Single(document.Frames);
        string uri = DictionaryNetworkPolicy.ResourceUriForDictionary(
            "dict",
            5,
            frame.ResourceCapability,
            "voice.mp3");

        ReaderResourceResponse response = await adapter.ResolveAsync(new(
            uri,
            null,
            null,
            null)
        {
            Method = "HEAD"
        });

        Assert.Equal(200, response.StatusCode);
        Assert.Empty(response.Body.ToArray());
        Assert.Equal("120", response.Headers["Content-Length"]);
        Assert.Equal("bytes", response.Headers["Accept-Ranges"]);
    }

    [Fact]
    public async Task MddInitializationDiagnosticsArePreservedInWebResponse()
    {
        FakeCatalog catalog = new() { Generation = 6 };
        catalog.ResourceHandler = (_, _, _) => Task.FromResult(
            new ResourceResolution(
                new ResourceContent("audio/mpeg", new byte[48]),
                "voice.mp3",
                "mdd:audio.mdd",
                null)
            {
                MddInitializationMilliseconds = 12.5,
                PersistentIndexHit = true
            });
        ReaderDocument document = Document(6, "dict");
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument(document);
        ReaderFrame frame = Assert.Single(document.Frames);
        string uri = DictionaryNetworkPolicy.ResourceUriForDictionary(
            "dict",
            6,
            frame.ResourceCapability,
            "voice.mp3");

        ReaderResourceResponse response = await adapter.ResolveAsync(new(
            uri,
            null,
            null,
            null));

        Assert.Equal("mdd:audio.mdd", response.Source);
        Assert.Equal(12.5, response.MddInitializationMilliseconds);
        Assert.True(response.PersistentIndexHit);
    }

    [Fact]
    public async Task ReadDiagnosticsArePreservedAcrossRangeSlicing()
    {
        FakeCatalog catalog = new() { Generation = 6 };
        ResourceReadDiagnostics diagnostics = new(
            SourceBytesRead: 100,
            RecordBlockBytesDecoded: 256,
            ResourceBytesMaterialized: 100,
            ResourceArrayCopyCount: 1,
            DecodedResourceCacheHit: false,
            RecordBlockCacheHit: false);
        catalog.ResourceHandler = (_, _, _) => Task.FromResult(
            new ResourceResolution(
                new ResourceContent("audio/mpeg", new byte[100]),
                "voice.mp3",
                "mdd:audio.mdd",
                null)
            {
                ReadDiagnostics = diagnostics
            });
        ReaderDocument document = Document(6, "dict");
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument(document);
        ReaderFrame frame = Assert.Single(document.Frames);
        string uri = DictionaryNetworkPolicy.ResourceUriForDictionary(
            "dict",
            6,
            frame.ResourceCapability,
            "voice.mp3");

        ReaderResourceResponse response = await adapter.ResolveAsync(new(
            uri,
            null,
            null,
            "bytes=0-24"));

        Assert.Equal(206, response.StatusCode);
        Assert.Equal(25, response.Body.Length);
        Assert.Equal(diagnostics, response.ReadDiagnostics);
    }

    [Fact]
    public async Task SyntheticLargeAudio_ExposesFullMaterializationBehindPartialResponse()
    {
        const int fullLength = 8 * 1024 * 1024;
        const int requestedLength = 1024 * 1024;
        FakeCatalog catalog = new() { Generation = 6 };
        catalog.ResourceHandler = (_, _, _) => Task.FromResult(
            new ResourceResolution(
                new ResourceContent("audio/wav", new byte[fullLength]),
                "stress.wav",
                "fixture",
                null)
            {
                ReadDiagnostics = new ResourceReadDiagnostics(
                    SourceBytesRead: fullLength,
                    RecordBlockBytesDecoded: 0,
                    ResourceBytesMaterialized: fullLength,
                    ResourceArrayCopyCount: 1,
                    DecodedResourceCacheHit: false,
                    RecordBlockCacheHit: null)
            });
        ReaderDocument document = Document(6, "dict");
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument(document);
        ReaderFrame frame = Assert.Single(document.Frames);
        string uri = DictionaryNetworkPolicy.ResourceUriForDictionary(
            "dict",
            6,
            frame.ResourceCapability,
            "stress.wav");

        ReaderResourceResponse response = await adapter.ResolveAsync(new(
            uri,
            null,
            null,
            $"bytes=0-{requestedLength - 1}"));

        Assert.Equal(206, response.StatusCode);
        Assert.Equal(fullLength, response.FullLength);
        Assert.Equal(requestedLength, response.Body.Length);
        Assert.Equal(
            $"bytes 0-{requestedLength - 1}/{fullLength}",
            response.Headers["Content-Range"]);
        Assert.Equal(fullLength, response.ReadDiagnostics.SourceBytesRead);
        Assert.Equal(fullLength, response.ReadDiagnostics.ResourceBytesMaterialized);
    }

    [Fact]
    public async Task CssLegacyUrls_AreUpgradedToSameOriginHttps()
    {
        byte[] css = System.Text.Encoding.UTF8.GetBytes(
            ".x{background:url(vdict://resource/dict/image.png?v=2#icon)}");
        FakeCatalog catalog = ResourceCatalog(9, "text/css; charset=utf-8", css);
        ReaderDocument document = Document(9, "dict");
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument(document);
        ReaderFrame frame = Assert.Single(document.Frames);
        string uri = DictionaryNetworkPolicy.ResourceUriForDictionary(
            "dict",
            9,
            frame.ResourceCapability,
            "styles/main.css");

        ReaderResourceResponse response = await adapter.ResolveAsync(new(
            uri,
            null,
            null,
            null));
        string rewritten = System.Text.Encoding.UTF8.GetString(response.Body.Span);

        Assert.Contains("vdict-doc://d-", rewritten);
        Assert.Contains($"/resource/9/{frame.ResourceCapability}/image.png?v=2#icon", rewritten);
        Assert.DoesNotContain("vdict:", rewritten);
    }

    [Fact]
    public async Task ConcurrentRequests_AreDeduplicatedByGenerationDictionaryAndPath()
    {
        FakeCatalog catalog = new() { Generation = 7 };
        TaskCompletionSource<ResourceResolution> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.ResourceHandler = async (_, _, _) => await completion.Task;
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument();
        string origin = DictionaryNetworkPolicy.FrameOriginForDictionary("dict");
        ReaderResourceRequest request = new(
            "vdict://resource/dict/audio/a.mp3",
            origin,
            null,
            null);

        Task<ReaderResourceResponse> first = adapter.ResolveAsync(request);
        Task<ReaderResourceResponse> second = adapter.ResolveAsync(request);
        await WaitUntilAsync(() => catalog.ResourceRequests == 1);
        completion.SetResult(new ResourceResolution(
            new ResourceContent("audio/mpeg", new byte[100]),
            "audio/a.mp3",
            "fixture",
            null));

        await Task.WhenAll(first, second);

        Assert.Equal(1, catalog.ResourceRequests);
    }

    [Fact]
    public async Task NewDocument_CancelsOldResourceWork()
    {
        FakeCatalog catalog = new();
        TaskCompletionSource canceled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.ResourceHandler = async (_, _, token) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
            catch (OperationCanceledException)
            {
                canceled.SetResult();
                throw;
            }

            throw new InvalidOperationException();
        };
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument();
        string origin = DictionaryNetworkPolicy.FrameOriginForDictionary("dict");
        Task<ReaderResourceResponse> old = adapter.ResolveAsync(new(
            "vdict://resource/dict/image.png",
            origin,
            null,
            null));
        await WaitUntilAsync(() => catalog.ResourceRequests == 1);

        adapter.BeginDocument();

        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
    }

    [Fact]
    public async Task Maintenance_CancelsAndDrainsInflightRequestsBeforeReturning()
    {
        FakeCatalog catalog = new();
        TaskCompletionSource cancellationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        catalog.ResourceHandler = async (_, _, token) =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                await release.Task;
                throw;
            }

            throw new InvalidOperationException();
        };
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument();
        Task<ReaderResourceResponse> request = adapter.ResolveAsync(new(
            "vdict://resource/dict/image.png",
            DictionaryNetworkPolicy.FrameOriginForDictionary("dict"),
            null,
            null));
        await WaitUntilAsync(() => catalog.ResourceRequests == 1);

        Task maintenance = adapter.BeginMaintenanceAsync();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.False(maintenance.IsCompleted);
        ReaderResourceResponse stale = await adapter.ResolveAsync(new(
            "vdict://resource/dict/late.png",
            DictionaryNetworkPolicy.FrameOriginForDictionary("dict"),
            null,
            null));
        Assert.Equal(410, stale.StatusCode);

        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await maintenance.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RangeResponse_ContainsRequiredSecurityAndLengthHeaders()
    {
        FakeCatalog catalog = new();
        catalog.ResourceHandler = (_, _, _) => Task.FromResult(new ResourceResolution(
            new ResourceContent("audio/mpeg", Enumerable.Range(0, 100).Select(i => (byte)i).ToArray()),
            "voice.mp3",
            "fixture",
            null));
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument();
        string origin = DictionaryNetworkPolicy.FrameOriginForDictionary("dict");

        ReaderResourceResponse response = await adapter.ResolveAsync(new(
            "vdict://resource/dict/voice.mp3",
            origin,
            null,
            "bytes=10-19"));

        Assert.Equal(206, response.StatusCode);
        Assert.Equal(10, response.Body.Length);
        Assert.Equal("bytes 10-19/100", response.Headers["Content-Range"]);
        Assert.Equal("10", response.Headers["Content-Length"]);
        Assert.Equal("bytes", response.Headers["Accept-Ranges"]);
        Assert.False(response.Headers.ContainsKey("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.ContainsKey("Vary"));
        Assert.Equal("same-origin", response.Headers["Cross-Origin-Resource-Policy"]);
        Assert.Equal("nosniff", response.Headers["X-Content-Type-Options"]);
        Assert.Equal("dict", response.DictionaryId);
        Assert.Equal("voice.mp3", response.NormalizedPath);
        Assert.Equal("fixture", response.Source);
        Assert.Equal(100, response.FullLength);
    }

    [Theory]
    [InlineData("bytes=0-1,4-5")]
    [InlineData("bytes=100-110")]
    [InlineData("items=0-9")]
    public async Task UnsupportedRange_Returns416(string range)
    {
        byte[] resourceBody = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        FakeCatalog catalog = new();
        catalog.ResourceHandler = (_, _, _) => Task.FromResult(new ResourceResolution(
            new ResourceContent("audio/mpeg", resourceBody),
            "voice.mp3",
            "fixture",
            null));
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument();

        ReaderResourceResponse response = await adapter.ResolveAsync(new(
            "vdict://resource/dict/voice.mp3",
            DictionaryNetworkPolicy.FrameOriginForDictionary("dict"),
            null,
            range));

        Assert.Equal(416, response.StatusCode);
        Assert.Empty(response.Body.ToArray());
        Assert.Equal("0", response.Headers["Content-Length"]);
        Assert.Equal("bytes */100", response.Headers["Content-Range"]);
    }

    [Fact]
    public void RemoteNetwork_IsAlwaysBlockedForEveryDictionary()
    {
        ReaderDocument document = new(
            "word",
            "",
            "token",
            [
                new ReaderFrame("a", new DictionaryId("blocked"), "") { BlockNetwork = true },
                new ReaderFrame("b", new DictionaryId("allowed"), "") { BlockNetwork = false }
            ],
            []);

        Assert.True(ReaderWebResourceAdapter.ShouldBlockRemote(
            "https://example.test/a.png",
            DictionaryNetworkPolicy.FrameOriginForDictionary("blocked"),
            null,
            document));
        Assert.True(ReaderWebResourceAdapter.ShouldBlockRemote(
            "https://example.test/a.png",
            DictionaryNetworkPolicy.FrameOriginForDictionary("allowed"),
            null,
            document));
    }

    [Fact]
    public async Task ResourceResolution_IsDispatchedOffCallingThread()
    {
        int callerThread = Environment.CurrentManagedThreadId;
        int resolverThread = callerThread;
        FakeCatalog catalog = new();
        catalog.ResourceHandler = (_, _, _) =>
        {
            resolverThread = Environment.CurrentManagedThreadId;
            return Task.FromResult(new ResourceResolution(
                new ResourceContent("image/png", new byte[8]),
                "image.png",
                "fixture",
                null));
        };
        using ReaderWebResourceAdapter adapter = new(catalog);
        adapter.BeginDocument();

        await adapter.ResolveAsync(new(
            "vdict://resource/dict/image.png",
            DictionaryNetworkPolicy.FrameOriginForDictionary("dict"),
            null,
            null));

        Assert.NotEqual(callerThread, resolverThread);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Test condition was not reached.");
            }

            await Task.Delay(10);
        }
    }

    private static ReaderDocument Document(long generation, params string[] dictionaries)
    {
        ImmutableArray<ReaderFrame> frames = dictionaries.Select((dictionary, index) =>
            new ReaderFrame($"frame-{index}", new DictionaryId(dictionary), "<p/>")
            {
                CatalogGeneration = generation,
                ResourceCapability = DictionaryNetworkPolicy.ResourceCapabilityForDictionary(
                    dictionary,
                    generation)
            }).ToImmutableArray();
        return new ReaderDocument("word", "", "token", frames, [])
        {
            CatalogGeneration = generation
        };
    }

    private static FakeCatalog ResourceCatalog(
        long generation,
        string mime,
        byte[] body)
    {
        FakeCatalog catalog = new() { Generation = generation };
        catalog.ResourceHandler = (_, path, _) => Task.FromResult(new ResourceResolution(
            new ResourceContent(mime, body),
            path,
            "fixture",
            null));
        return catalog;
    }
}
