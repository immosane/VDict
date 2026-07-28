using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using VDict.Core.Catalog;
using VDict.Core.Models;
using VDict.Core.Policies;

namespace VDict.Presentation.Services;

public sealed record ReaderResourceRequest(
    string Uri,
    string? Origin,
    string? Referer,
    string? Range)
{
    public string Method { get; init; } = "GET";
}

public sealed record ReaderResourceResponse(
    int StatusCode,
    string ReasonPhrase,
    string MimeType,
    ReadOnlyMemory<byte> Body,
    IReadOnlyDictionary<string, string> Headers)
{
    public string? Diagnostic { get; init; }

    public string? DictionaryId { get; init; }

    public long Generation { get; init; }

    public string? NormalizedPath { get; init; }

    public string? LookupKey { get; init; }

    public string? Source { get; init; }

    public int FullLength { get; init; }

    public double? MddInitializationMilliseconds { get; init; }

    public bool? PersistentIndexHit { get; init; }

    public ResourceReadDiagnostics ReadDiagnostics { get; init; } =
        ResourceReadDiagnostics.None;
}

public sealed partial class ReaderWebResourceAdapter : IDisposable
{
    private readonly IDictionaryCatalog _catalog;
    private readonly ConcurrentDictionary<ResourceKey, Lazy<Task<ResourceResolution>>> _inflight = new();
    private readonly object _documentSync = new();
    private CancellationTokenSource _documentCancellation = new();
    private ImmutableArray<ResourceGrant> _grants = [];
    private long _generation;
    private int _activeRequests;
    private bool _acceptingRequests = true;
    private TaskCompletionSource _requestsDrained = CompletedDrain();
    private bool _disposed;

    public ReaderWebResourceAdapter(IDictionaryCatalog catalog)
    {
        _catalog = catalog;
        _generation = catalog.GetSnapshot().Generation;
    }

    public void BeginDocument(ReaderDocument? document = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancellationTokenSource old;
        DictionaryCatalogSnapshot snapshot = _catalog.GetSnapshot();
        long generation = document?.CatalogGeneration ?? snapshot.Generation;
        ImmutableArray<ResourceGrant> grants = document is null
            ? snapshot.ActiveDictionaries.Select(dictionary => new ResourceGrant(
                    dictionary.Id,
                    generation,
                    DictionaryNetworkPolicy.ResourceCapabilityForDictionary(
                        dictionary.Id.Value,
                        generation)))
                .ToImmutableArray()
            : document.Frames.Select(frame => new ResourceGrant(
                    frame.DictionaryId,
                    frame.CatalogGeneration,
                    frame.ResourceCapability))
                .Distinct()
                .ToImmutableArray();
        lock (_documentSync)
        {
            old = _documentCancellation;
            _documentCancellation = new CancellationTokenSource();
            _generation = generation;
            _grants = grants;
            _acceptingRequests = true;
            _inflight.Clear();
        }

        old.Cancel();
        old.Dispose();
    }

    public Task BeginMaintenanceAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancellationTokenSource cancellation;
        Task drained;
        lock (_documentSync)
        {
            _acceptingRequests = false;
            _grants = [];
            _inflight.Clear();
            cancellation = _documentCancellation;
            drained = _requestsDrained.Task;
        }

        cancellation.Cancel();
        return drained;
    }

    public Task<ReaderResourceResponse> ResolveAsync(ReaderResourceRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_documentSync)
        {
            if (!_acceptingRequests)
            {
                return Task.FromResult(Error(410, "Gone"));
            }

            if (_activeRequests++ == 0)
            {
                _requestsDrained = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return ResolveTrackedAsync(request);
    }

    private async Task<ReaderResourceResponse> ResolveTrackedAsync(ReaderResourceRequest request)
    {
        try
        {
            return await ResolveCoreAsync(request).ConfigureAwait(false);
        }
        finally
        {
            TaskCompletionSource? completed = null;
            lock (_documentSync)
            {
                if (--_activeRequests == 0)
                {
                    completed = _requestsDrained;
                }
            }

            completed?.TrySetResult();
        }
    }

    private async Task<ReaderResourceResponse> ResolveCoreAsync(ReaderResourceRequest request)
    {
        if (!TryAuthorize(
                request,
                out ResourceGrant grant,
                out string normalizedPath,
                out string? authorizationDiagnostic,
                out ReaderResourceResponse? authorizationError))
        {
            return authorizationError!;
        }

        CancellationToken token;
        long generation;
        lock (_documentSync)
        {
            token = _documentCancellation.Token;
            generation = _generation;
        }

        if (generation != grant.Generation ||
            _catalog.GetSnapshot().Generation != grant.Generation)
        {
            return Error(410, "Gone");
        }

        ResourceKey key = new(
            generation,
            grant.DictionaryId,
            normalizedPath.ToUpperInvariant());
        Lazy<Task<ResourceResolution>> lazy = _inflight.GetOrAdd(
            key,
            _ => new Lazy<Task<ResourceResolution>>(
                () => Task.Run(
                    () => _catalog.ResolveResourceAsync(
                        grant.DictionaryId,
                        normalizedPath,
                        token),
                    token),
                LazyThreadSafetyMode.ExecutionAndPublication));
        ResourceResolution resolution;
        try
        {
            resolution = await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inflight.TryRemove(
                new KeyValuePair<ResourceKey, Lazy<Task<ResourceResolution>>>(key, lazy));
        }

        token.ThrowIfCancellationRequested();
        if (resolution.Resource is not { } resource)
        {
            return Error(404, "Not Found") with
            {
                Diagnostic = resolution.FailureReason,
                DictionaryId = grant.DictionaryId.Value,
                Generation = grant.Generation,
                NormalizedPath = resolution.NormalizedPath.Length > 0
                    ? resolution.NormalizedPath
                    : normalizedPath,
                LookupKey = resolution.LookupKey,
                Source = resolution.Source,
                MddInitializationMilliseconds = resolution.MddInitializationMilliseconds,
                PersistentIndexHit = resolution.PersistentIndexHit,
                ReadDiagnostics = resolution.ReadDiagnostics
            };
        }

        ReadOnlyMemory<byte> fullBody = RewriteCssResource(
            resource,
            grant);
        Dictionary<string, string> headers = SecurityHeaders();
        ReadOnlyMemory<byte> responseBody = fullBody;
        int status = 200;
        string reason = "OK";
        if (!string.IsNullOrWhiteSpace(request.Range))
        {
            if (!HttpByteRangeParser.TryParse(
                    request.Range,
                    fullBody.Length,
                    out int start,
                    out int end))
            {
                headers["Content-Range"] = $"bytes */{fullBody.Length}";
                headers["Content-Length"] = "0";
                return new(
                    416,
                    "Range Not Satisfiable",
                    resource.MimeType,
                    ReadOnlyMemory<byte>.Empty,
                    headers)
                {
                    Diagnostic = "Rejected unsupported or unsatisfiable byte range.",
                    DictionaryId = grant.DictionaryId.Value,
                    Generation = grant.Generation,
                    NormalizedPath = resolution.NormalizedPath,
                    LookupKey = resolution.LookupKey,
                    Source = resolution.Source,
                    FullLength = fullBody.Length,
                    MddInitializationMilliseconds = resolution.MddInitializationMilliseconds,
                    PersistentIndexHit = resolution.PersistentIndexHit,
                    ReadDiagnostics = resolution.ReadDiagnostics
                };
            }

            int length = end - start + 1;
            responseBody = fullBody.Slice(start, length);
            status = 206;
            reason = "Partial Content";
            headers["Content-Range"] = $"bytes {start}-{end}/{fullBody.Length}";
        }

        headers["Content-Length"] = responseBody.Length.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        if (request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            headers["Content-Length"] = (status == 206 ? responseBody.Length : fullBody.Length)
                .ToString(System.Globalization.CultureInfo.InvariantCulture);
            responseBody = ReadOnlyMemory<byte>.Empty;
        }

        return new(status, reason, resource.MimeType, responseBody, headers)
        {
            Diagnostic = authorizationDiagnostic ?? resolution.FailureReason,
            DictionaryId = grant.DictionaryId.Value,
            Generation = grant.Generation,
            NormalizedPath = resolution.NormalizedPath,
            LookupKey = resolution.LookupKey,
            Source = resolution.Source,
            FullLength = fullBody.Length,
            MddInitializationMilliseconds = resolution.MddInitializationMilliseconds,
            PersistentIndexHit = resolution.PersistentIndexHit,
            ReadDiagnostics = resolution.ReadDiagnostics
        };
    }

    public static bool ShouldBlockRemote(
        string uri,
        string? origin,
        string? referer,
        ReaderDocument? document)
    {
        _ = origin;
        _ = referer;
        _ = document;
        return DictionaryNetworkPolicy.ShouldBlock(uri);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancellationTokenSource cancellation;
        lock (_documentSync)
        {
            cancellation = _documentCancellation;
            _grants = [];
            _acceptingRequests = false;
            _inflight.Clear();
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private static TaskCompletionSource CompletedDrain()
    {
        TaskCompletionSource completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        completion.TrySetResult();
        return completion;
    }

    private bool TryAuthorize(
        ReaderResourceRequest request,
        out ResourceGrant grant,
        out string normalizedPath,
        out string? diagnostic,
        out ReaderResourceResponse? error)
    {
        grant = default;
        normalizedPath = string.Empty;
        diagnostic = null;
        error = null;
        ImmutableArray<ResourceGrant> grants;
        lock (_documentSync)
        {
            grants = _grants;
        }

        if (DictionaryNetworkPolicy.TryParseResourceUri(
                request.Uri,
                out string host,
                out long generation,
                out string capability,
                out string path))
        {
            grant = grants.FirstOrDefault(candidate =>
                candidate.Generation == generation &&
                candidate.Capability.Equals(capability, StringComparison.Ordinal));
            if (grant == default ||
                !DictionaryNetworkPolicy.IsAuthorizedResourceRequest(
                    grant.DictionaryId.Value,
                    grant.Generation,
                    grant.Capability,
                    host,
                    generation,
                    capability))
            {
                error = Error(403, "Forbidden");
                return false;
            }

            if (!ResourcePathPolicy.TryNormalize(path, out normalizedPath))
            {
                error = Error(400, "Bad Request");
                return false;
            }

            string expectedOrigin = DictionaryNetworkPolicy.FrameOriginForDictionary(
                grant.DictionaryId.Value);
            if ((!string.IsNullOrWhiteSpace(request.Origin) &&
                 !request.Origin.TrimEnd('/').Equals(
                     expectedOrigin,
                     StringComparison.OrdinalIgnoreCase)) ||
                (string.IsNullOrWhiteSpace(request.Origin) &&
                 Uri.TryCreate(request.Referer, UriKind.Absolute, out Uri? referer) &&
                 !referer.GetLeftPart(UriPartial.Authority).Equals(
                     expectedOrigin,
                     StringComparison.OrdinalIgnoreCase)))
            {
                diagnostic = "Resource capability origin or referer did not match the dictionary frame.";
                error = Error(403, "Forbidden") with { Diagnostic = diagnostic };
                return false;
            }

            return true;
        }

        if (DictionaryNetworkPolicy.TryParseLegacyResourceUri(
                request.Uri,
                out string dictionaryValue,
                out string legacyPath) &&
            DictionaryId.TryParse(dictionaryValue, out DictionaryId dictionaryId) &&
            ResourcePathPolicy.TryNormalize(legacyPath, out normalizedPath))
        {
            grant = grants.FirstOrDefault(candidate => candidate.DictionaryId == dictionaryId);
            if (grant == default ||
                !DictionaryNetworkPolicy.IsAuthorizedResourceRequest(
                    dictionaryId.Value,
                    request.Origin,
                    request.Referer))
            {
                error = Error(403, "Forbidden");
                return false;
            }

            diagnostic = "Legacy vdict resource fallback was used.";
            return true;
        }

        error = Error(400, "Bad Request");
        return false;
    }

    private static ReadOnlyMemory<byte> RewriteCssResource(
        ResourceContent resource,
        ResourceGrant grant)
    {
        if (!resource.MimeType.StartsWith("text/css", StringComparison.OrdinalIgnoreCase))
        {
            return resource.Body;
        }

        string css = Encoding.UTF8.GetString(resource.Body.Span);
        string rewritten = LegacyResourceUrl().Replace(
            css,
            match =>
            {
                string value = match.Value;
                if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                    !DictionaryNetworkPolicy.TryParseLegacyResourceUri(
                        value,
                        out string dictionaryId,
                        out string path) ||
                    !dictionaryId.Equals(
                        grant.DictionaryId.Value,
                        StringComparison.Ordinal))
                {
                    return "about:blank";
                }

                return DictionaryNetworkPolicy.ResourceUriForDictionary(
                    dictionaryId,
                    grant.Generation,
                    grant.Capability,
                    path,
                    uri.Query.TrimStart('?'),
                    uri.Fragment.TrimStart('#'));
            });
        return Encoding.UTF8.GetBytes(rewritten);
    }

    private static Dictionary<string, string> SecurityHeaders() => new()
    {
        ["Accept-Ranges"] = "bytes",
        ["Cross-Origin-Resource-Policy"] = "same-origin",
        ["X-Content-Type-Options"] = "nosniff",
        ["Cache-Control"] = "public, max-age=31536000, immutable"
    };

    private static ReaderResourceResponse Error(int status, string reason) => new(
        status,
        reason,
        "application/octet-stream",
        ReadOnlyMemory<byte>.Empty,
        new Dictionary<string, string>
        {
            ["Cache-Control"] = "no-store",
            ["Content-Length"] = "0",
            ["X-Content-Type-Options"] = "nosniff"
        });

    [GeneratedRegex(@"vdict://resource/[^\s'\""()<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyResourceUrl();

    private readonly record struct ResourceGrant(
        DictionaryId DictionaryId,
        long Generation,
        string Capability);

    private readonly record struct ResourceKey(
        long Generation,
        DictionaryId DictionaryId,
        string Path);
}
