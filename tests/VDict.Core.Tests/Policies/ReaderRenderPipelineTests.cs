using System.Collections.Concurrent;
using VDict.Core.Policies;

namespace VDict.Core.Tests.Policies;

public sealed class ReaderRenderPipelineTests
{
    [Fact]
    public async Task FirstDocumentNavigatesExactlyOnce()
    {
        Harness harness = new();
        (string document, long version) = harness.Register("v1");

        await harness.RenderAsync(document, version);
        await harness.RenderAsync(document, version);

        Assert.Equal(1, harness.StopMediaCount);
        Assert.Equal(new[] { "v1" }, harness.BegunDocuments);
        Assert.Equal(new[] { "v1" }, harness.NavigatedDocuments);
        Assert.Equal("v1", harness.CurrentDocument);
        Assert.Equal(version, harness.State.LastRenderedVersion);
    }

    [Fact]
    public async Task LoadedAndSizeChangedCoalesceSameVersion()
    {
        Harness harness = new();
        (string document, long version) = harness.Register("v1");
        TaskCompletionSource stopEntered = NewSignal();
        TaskCompletionSource releaseStop = NewSignal();
        harness.StopMedia = async () =>
        {
            stopEntered.TrySetResult();
            await releaseStop.Task;
        };

        Task first = harness.RenderAsync(document, version);
        await stopEntered.Task;
        Task second = harness.RenderAsync(document, version);
        releaseStop.TrySetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(new[] { "v1" }, harness.NavigatedDocuments);
        Assert.Contains(ReaderRenderStage.StaleOrDuplicateAfterStop, harness.Stages);
    }

    [Fact]
    public async Task NewVersionDuringStopMediaSupersedesOld()
    {
        Harness harness = new();
        (string oldDocument, long oldVersion) = harness.Register("A");
        TaskCompletionSource stopEntered = NewSignal();
        TaskCompletionSource releaseStop = NewSignal();
        harness.StopMedia = async () =>
        {
            stopEntered.TrySetResult();
            await releaseStop.Task;
        };

        Task oldRender = harness.RenderAsync(oldDocument, oldVersion);
        await stopEntered.Task;
        (string newDocument, long newVersion) = harness.Register("B");
        releaseStop.TrySetResult();
        await oldRender;
        await harness.RenderAsync(newDocument, newVersion);

        Assert.Equal(new[] { "B" }, harness.BegunDocuments);
        Assert.Equal(new[] { "B" }, harness.NavigatedDocuments);
        Assert.Equal("B", harness.CurrentDocument);
    }

    [Fact]
    public async Task RapidDocumentsOnlyLatestNavigates()
    {
        Harness harness = new();
        (string a, long av) = harness.Register("A");
        (string b, long bv) = harness.Register("B");
        (string c, long cv) = harness.Register("C");

        await Task.WhenAll(
            harness.RenderAsync(a, av),
            harness.RenderAsync(b, bv),
            harness.RenderAsync(c, cv));

        Assert.Equal(new[] { "C" }, harness.BegunDocuments);
        Assert.Equal(new[] { "C" }, harness.NavigatedDocuments);
        Assert.Equal("C", harness.CurrentDocument);
    }

    [Fact]
    public async Task CancellationAfterStopDoesNotConsumeVersion()
    {
        Harness harness = new();
        (string document, long version) = harness.Register("v1");
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource stopEntered = NewSignal();
        TaskCompletionSource releaseStop = NewSignal();
        harness.StopMedia = async () =>
        {
            stopEntered.TrySetResult();
            await releaseStop.Task;
        };

        Task render = harness.RenderAsync(document, version, cancellation.Token);
        await stopEntered.Task;
        cancellation.Cancel();
        releaseStop.TrySetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(() => render);
        Assert.Equal(0, harness.State.LastRenderedVersion);
        Assert.Empty(harness.BegunDocuments);
        Assert.Empty(harness.NavigatedDocuments);
    }

    [Fact]
    public async Task BrowserRecoveryRenavigatesLatestDocument()
    {
        Harness harness = new();
        (string document, long initialVersion) = harness.Register("v1");
        await harness.RenderAsync(document, initialVersion);

        harness.ClearNavigationEvidence();
        (string recoveryDocument, long recoveryVersion) = harness.Register(document);
        await harness.RenderAsync(recoveryDocument, recoveryVersion);

        Assert.True(recoveryVersion > initialVersion);
        Assert.Equal(new[] { "v1" }, harness.NavigatedDocuments);
    }

    [Fact]
    public async Task RetryAfterNavigationFailureNavigatesNewVersion()
    {
        Harness harness = new();
        (string document, long failedVersion) = harness.Register("v1");
        await harness.RenderAsync(document, failedVersion);

        harness.ClearNavigationEvidence();
        (string retryDocument, long retryVersion) = harness.Register(document);
        await harness.RenderAsync(retryDocument, retryVersion);

        Assert.True(retryVersion > failedVersion);
        Assert.Equal(new[] { "v1" }, harness.NavigatedDocuments);
    }

    [Fact]
    public async Task RenderOrderingPreservesResourceAuthorization()
    {
        Harness harness = new();
        (string document, long version) = harness.Register("v1");

        await harness.RenderAsync(document, version);

        Assert.Equal(
            new[] { "StopMedia", "BeginDocument:v1", "Navigate:v1" },
            harness.Calls);
    }

    [Fact]
    public async Task StopMediaFailureReleasesNavigationGate()
    {
        Harness harness = new()
        {
            StopMedia = () => throw new InvalidOperationException("stop failed")
        };
        (string failedDocument, long failedVersion) = harness.Register("A");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.RenderAsync(failedDocument, failedVersion));

        harness.StopMedia = () => Task.CompletedTask;
        (string latestDocument, long latestVersion) = harness.Register("B");
        await harness.RenderAsync(latestDocument, latestVersion).WaitAsync(
            TimeSpan.FromSeconds(1));

        Assert.Equal(new[] { "B" }, harness.NavigatedDocuments);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Harness
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public ReaderNavigationState State { get; } = new();
        public Func<Task> StopMedia { get; set; } = () => Task.CompletedTask;
        public bool CanRender { get; set; } = true;
        public int StopMediaCount { get; private set; }
        public string? CurrentDocument { get; private set; }
        public List<string> BegunDocuments { get; } = [];
        public List<string> NavigatedDocuments { get; } = [];
        public List<string> Calls { get; } = [];
        public ConcurrentQueue<ReaderRenderStage> Stages { get; } = new();

        public (string Document, long Version) Register(string document) =>
            (document, State.RegisterDocument());

        public async Task RenderAsync(
            string document,
            long version,
            CancellationToken cancellationToken = default)
        {
            if (!State.ShouldRender(version))
            {
                return;
            }

            _ = await ReaderRenderPipeline.ExecuteAsync(
                version,
                State,
                _gate,
                () => CanRender,
                async () =>
                {
                    StopMediaCount++;
                    Calls.Add("StopMedia");
                    await StopMedia();
                },
                () =>
                {
                    CurrentDocument = document;
                    BegunDocuments.Add(document);
                    Calls.Add($"BeginDocument:{document}");
                    NavigatedDocuments.Add(document);
                    Calls.Add($"Navigate:{document}");
                },
                Stages.Enqueue,
                cancellationToken);
        }

        public void ClearNavigationEvidence()
        {
            CurrentDocument = null;
            BegunDocuments.Clear();
            NavigatedDocuments.Clear();
            Calls.Clear();
            Stages.Clear();
            StopMediaCount = 0;
        }
    }
}
