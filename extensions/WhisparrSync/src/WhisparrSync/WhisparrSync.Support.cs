using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Ingest;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using WhisparrSync.Reconcile;
using WhisparrSync.State;
using static Cove.Extensions.Shared.MinimalApiPermissions;

namespace WhisparrSync;

/// <summary>
/// Shared request-handling plumbing: scoped-library access, safe Cove-video loads, id/kind/scope
/// parsing, the import-log projection + sync-health, the reconcile trigger, and the TTL list caches.
/// </summary>
public sealed partial class WhisparrSync
{
    // Builds a scoped ICoveLibraryPort for the bulk-add-missing local enumeration and runs <paramref name="body"/>
    // inside the DB scope so the port's DbContext has the correct lifetime (never a long-lived captured context).
    // Degrades to an empty port when no host DB scope is available (mirrors LoadVideoByIdSafeAsync's null-scope
    // guard): with no scope the entity has no enumerable scenes, so add-all-missing finds nothing to register.
    private async Task<IResult> WithScopedLibraryAsync(string stashEndpoint, string tpdbEndpoint, Func<ICoveLibraryPort, Task<IResult>> body)
    {
        if (_scopeFactory is null)
        {
            return await body(EmptyCoveLibraryPort.Instance);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        return scope.ServiceProvider.GetService<DbContext>() is not { } db
            ? await body(EmptyCoveLibraryPort.Instance)
            : await body(new CoveLibraryPort(db, stashEndpoint, tpdbEndpoint));
    }

    // SceneActions requires an ICoveLibraryPort, but the per-scene + search operations (add/search/monitor/
    // search-all) never touch it — only AddAllMissing enumerates an entity's scenes. This no-op port satisfies
    // the constructor for those paths (the per-scene handlers resolve the scene via LoadVideoByIdSafeAsync, and
    // search-all needs no local enumeration); a scope-backed CoveLibraryPort is used only for add-all-missing.
    private sealed class EmptyCoveLibraryPort : ICoveLibraryPort
    {
        public static readonly EmptyCoveLibraryPort Instance = new();

        public Task<IReadOnlyList<CoveVideo>> LoadAllVideosAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveVideo>>([]);

        public Task<CoveVideo?> LoadVideoByIdAsync(int coveId, CancellationToken ct = default)
            => Task.FromResult<CoveVideo?>(null);

        public Task<IReadOnlyList<CoveVideo>> LoadVideosByIdsAsync(
            IReadOnlyList<int> coveIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveVideo>>([]);

        public Task<IReadOnlyList<CoveVideo>> LoadVideosForEntityAsync(
            EntityKind kind, int coveEntityId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveVideo>>([]);

        public Task<CoveEntityIdentity?> LoadEntityIdentityAsync(
            EntityKind kind, int coveEntityId, CancellationToken ct = default)
            => Task.FromResult<CoveEntityIdentity?>(null);

        public Task<IReadOnlyList<CoveEntityIdentity>> LoadAllEntityIdentitiesAsync(
            EntityKind kind, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CoveEntityIdentity>>([]);
    }

    // The first of the scene's StashDB ids that indexes a Whisparr movie (id order preserved), or null when
    // none match — the same lookup rule SceneStatusProjector uses, applied here to reach the movie id.
    private static WhisparrMovie? ResolveMovieForScene(
        IReadOnlyList<string> stashIds, IReadOnlyDictionary<string, WhisparrMovie> movieIndex)
    {
        foreach (var id in stashIds)
        {
            if (!string.IsNullOrEmpty(id) && movieIndex.TryGetValue(id, out var movie))
            {
                return movie;
            }
        }

        return null;
    }

    // Loads every Cove video for the scene-status summary, degrading to an empty list when no host DB scope is
    // available (mirrors GetCoveRootsAsync's defensive null-scope check). In production InitializeAsync always
    // captures the scope factory; the guard keeps the read-only summary from throwing if it never did.
    private async Task<IReadOnlyList<CoveVideo>> LoadAllVideosSafeAsync(string stashEndpoint, string tpdbEndpoint, CancellationToken ct)
    {
        if (_scopeFactory is null)
        {
            return [];
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<DbContext>() is not { } db)
        {
            return [];
        }

        return await new CoveLibraryPort(db, stashEndpoint, tpdbEndpoint).LoadAllVideosAsync(ct);
    }

    // The library-wide identity-health count (total scenes + how many carry no connected-version provider id),
    // computed in one AsNoTracking load. The read runs as CovePrincipal.System() for its whole span: a
    // settings-page request can arrive under any principal, and CoveContext applies per-principal authz query
    // filters, so a non-System read would undercount (an Anonymous one returns zero rows — the "0 of 0" trap).
    // The prior principal is restored so the surrounding request is untouched. Degrades to (0,0) with no scope.
    private async Task<(int Total, int Unidentified)> CountIdentityHealthAsync(WhisparrOptions options, CancellationToken ct)
    {
        if (_scopeFactory is null)
        {
            return (0, 0);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<DbContext>() is not { } db)
        {
            return (0, 0);
        }

        var principals = scope.ServiceProvider.GetService<ICurrentPrincipalAccessor>();
        var previousPrincipal = principals?.Current;
        principals?.Set(CovePrincipal.System());
        try
        {
            var videos = await new CoveLibraryPort(db, options.StashDbEndpoint, options.TpdbEndpoint).LoadAllVideosAsync(ct);
            var useTpdb = string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase);
            return IdentityHealth.Count(videos, useTpdb);
        }
        finally
        {
            principals?.Set(previousPrincipal);
        }
    }

    // Resolves one Cove video by id for the scene-detail/releases reads, degrading to null when no host DB scope
    // is available (same defensive null-scope check as LoadAllVideosSafeAsync). A null result is the caller's
    // NO_STASHDB_IDENTITY outcome — a not-resolvable scene never reaches Whisparr.
    private async Task<CoveVideo?> LoadVideoByIdSafeAsync(int coveId, string stashEndpoint, string tpdbEndpoint, CancellationToken ct)
    {
        if (_scopeFactory is null)
        {
            return null;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        if (scope.ServiceProvider.GetService<DbContext>() is not { } db)
        {
            return null;
        }

        return await new CoveLibraryPort(db, stashEndpoint, tpdbEndpoint).LoadVideoByIdAsync(coveId, ct);
    }

    /// <summary>
    /// Parses the request <c>Kind</c> (<c>"studio"</c> / <c>"performer"</c>, case-insensitive) into the
    /// <see cref="EntityKind"/>. Returns <c>false</c> for anything else so the caller rejects an
    /// unknown kind with a 400 rather than guessing.
    /// </summary>
    private static bool TryParseEntityKind(string? kind, out EntityKind entityKind)
    {
        if (string.Equals(kind, "studio", StringComparison.OrdinalIgnoreCase))
        {
            entityKind = EntityKind.Studio;
            return true;
        }

        if (string.Equals(kind, "performer", StringComparison.OrdinalIgnoreCase))
        {
            entityKind = EntityKind.Performer;
            return true;
        }

        entityKind = default;
        return false;
    }

    // Parse the wire scope ("NewReleases"/"AllScenes", case-insensitive) to the enum; any absent or
    // unrecognized value falls back to the stored default so a malformed body never throws.
    private static MonitorScope ParseMonitorScope(string? scope, MonitorScope fallback)
        => Enum.TryParse<MonitorScope>(scope, ignoreCase: true, out var parsed) ? parsed : fallback;

    /// <summary>
    /// Resolves the entity's Whisparr lookup id from the forwarded Cove <paramref name="remoteIds"/>: the
    /// <c>RemoteId</c> whose <c>Endpoint</c> case-insensitively equals the stored
    /// <paramref name="stashDbEndpoint"/> — the SAME rule <see cref="CoveLibraryPort"/> uses to resolve a
    /// video's StashDB id, keeping the endpoint match a single server-side source of truth. Returns
    /// <c>null</c> when the entity carries no StashDB identity (the caller reports it cannot be monitored).
    /// </summary>
    private static string? ResolveStashId(RemoteIdInput[]? remoteIds, string stashDbEndpoint)
        => remoteIds?
            .FirstOrDefault(r =>
                !string.IsNullOrWhiteSpace(r.RemoteId)
                && string.Equals(r.Endpoint, stashDbEndpoint, StringComparison.OrdinalIgnoreCase))
            ?.RemoteId;

    /// <summary>
    /// Resolves the entity's Whisparr lookup id by the CONNECTED version's endpoint
    /// (<see cref="WhisparrOptions.IdentityEndpoint"/>): StashDB on v3, ThePornDB on v2. The version selection is
    /// the layer over the <see cref="ResolveStashId"/> endpoint-match primitive; a v2 connection therefore
    /// resolves the TPDB site id, never a StashDB id the v2 instance cannot know. Returns <c>null</c> when the
    /// entity carries no id for the connected version (the caller reports the handled no-identity outcome).
    /// </summary>
    private static string? ResolveRemoteId(RemoteIdInput[]? remoteIds, WhisparrOptions options)
        => ResolveStashId(remoteIds, options.IdentityEndpoint);

    // The connected version's identity provider name — the name the missing-id guard + Lifecycle surface to
    // the user. Derived from SelectedVersion (v3 keys on StashDB, v2 on ThePornDB), never a hardcoded literal
    // at a call site, so a v2 connection is never mislabeled "StashDB".
    private static string ProviderNameFor(WhisparrOptions options)
        => string.Equals(options.SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase) ? "ThePornDB" : "StashDB";

    /// <summary>
    /// Maps a monitor / status result to the wire response: <c>Ok</c> serializes the value (camelCase); a v2
    /// deferral (<see cref="WhisparrResultState.VersionMismatch"/>) returns a clear <c>400</c>
    /// <c>VERSION_UNSUPPORTED</c> (never a 500 — Whisparr v2 is unsupported); any other non-Ok maps to a
    /// <c>502</c> with the failure discriminator (never leaking the key or a raw reason).
    /// </summary>
    private static IResult ToMonitorResult<T>(WhisparrResult<T> result) => result.State switch
    {
        WhisparrResultState.Ok => Results.Json(result.Value, MonitorResponseJsonOptions),
        WhisparrResultState.VersionMismatch => Results.Json(
            new { code = "VERSION_UNSUPPORTED", detected = result.DetectedVersion }, statusCode: 400),
        // Rejected carries Whisparr's own error text (safe to surface — not the key/URL), so pass it through.
        WhisparrResultState.Rejected => Results.Json(
            new { result = "rejected", message = result.Reason }, statusCode: 502),
        _ => Results.Json(new { result = FailureDiscriminator(result.State) }, statusCode: 502),
    };

    /// <summary>
    /// Resolves the Cove library roots for the overlap check. Preferred source:
    /// <c>CoveConfiguration.CovePaths</c> resolved from a fresh scope when the host injects it. Fallback: the
    /// distinct parent folders of the library's own file paths via <see cref="CoveLibraryPort"/> — enough for
    /// an advisory containment comparison. Returns an empty set (no warning) when neither source is available;
    /// the overlap warning is advisory, so an unavailable source degrades to silence, never an error.
    /// </summary>
    private async Task<IReadOnlyList<string>> GetCoveRootsAsync(CancellationToken ct)
    {
        if (_scopeFactory is null)
        {
            return [];
        }

        await using var scope = _scopeFactory.CreateAsyncScope();

        // Preferred: the host-configured media-library roots ScanService scans (VERIFIED to exist).
        if (scope.ServiceProvider.GetService<CoveConfiguration>() is { CovePaths: { Count: > 0 } covePaths })
        {
            return [.. covePaths
                .Select(p => p.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }

        // Fallback: derive distinct folders from the library's own files (needs no host-config access).
        if (scope.ServiceProvider.GetService<DbContext>() is not { } db)
        {
            return [];
        }

        var options = await new OptionsStore(Store).LoadAsync(ct);
        var videos = await new CoveLibraryPort(db, options.StashDbEndpoint, options.TpdbEndpoint).LoadAllVideosAsync(ct);
        return [.. videos
            .SelectMany(v => v.FilePaths)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetDirectoryName(p) ?? p)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Returns the auto-import audit log — every attempt with its result, source, time, path, and Cove item —
    /// plus imported/skipped/flagged/total counts (review half). A pure read of the extension's own
    /// journal (reaches no credentials, opens no scope). 403-first on <c>extensions.read</c>.
    /// </summary>
    internal async Task<IResult> ImportLogAsync(ICurrentPrincipalAccessor principal, CancellationToken ct)
    {
        if (Forbidden(principal, Permissions.ExtensionsRead) is { } denied)
        {
            return denied;
        }

        var entries = await new ImportLog(Store).LoadAllAsync(ct);
        var counts = new ImportLogCounts(
            Imported: entries.Count(e => e.Result == "Imported"),
            Skipped: entries.Count(e => e.Result == "Skipped"),
            Flagged: entries.Count(e => e.Result == "Flagged"),
            Total: entries.Count);
        return Results.Json(new { entries, counts, syncHealth = SyncHealthOf(entries) }, ImportLogResponseJsonOptions);
    }

    /// <summary>
    /// The "sync is broken" signal for the settings banner: path-mismatch import failures (Whisparr reported a
    /// path Cove can't open) that happened AFTER the last successful import — so a later success clears it and a
    /// stale one-off doesn't nag forever. Returns the unresolved count, the newest sample paths, and its ticks.
    /// </summary>
    internal static SyncHealthView SyncHealthOf(IReadOnlyList<ImportLogEntry> entries)
    {
        var lastSuccessTicks = entries.Where(e => e.Result == "Imported").Select(e => e.UtcTicks).DefaultIfEmpty(0L).Max();
        var unresolved = entries
            .Where(e => e.Reason == IngestCoordinator.PathNotVisibleReason && e.UtcTicks > lastSuccessTicks)
            .ToList();
        return new SyncHealthView(
            PathMismatch: unresolved.Count,
            LastMismatchTicks: unresolved.Count > 0 ? unresolved.Max(e => e.UtcTicks) : null,
            SamplePaths: [.. unresolved
                .OrderByDescending(e => e.UtcTicks)
                .Select(e => e.Path)
                .Distinct(StringComparer.Ordinal)
                .Take(3)]);
    }

    /// <summary>
    /// One reconcile pass, run inside the enqueued exclusive job (the scheduler's work delegate). Resolves the
    /// stored creds (stored key only against the stored host) and the request-scoped
    /// <see cref="WhisparrClient"/>, then runs the <see cref="ReconcileJob"/> over the SAME
    /// <see cref="IngestCoordinator"/> + Whisparr-root guard the webhook uses. A no-op until configured.
    /// </summary>
    internal async Task RunReconcileAsync(CancellationToken ct)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<WhisparrClient>();

        var (_, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            return; // not configured yet — nothing to reconcile against
        }

        var coordinator = new IngestCoordinator(ScopeFactory, c => GetWhisparrRootsAsync(client, c));
        var job = new ReconcileJob(
            Store, coordinator, (page, c) => client.ListHistoryAsync(baseUrl, apiKey, page, ReconcileJob.PageSize, c));
        await job.RunAsync(ct);
    }

    // A short in-memory cache of the Whisparr root folders (they change rarely): the webhook ingest guard
    // consults this per event, so it must never issue an uncached GET per event. Fail-closed — a failed fetch
    // leaves the cache untouched and returns no roots, so the containment guard rejects until roots are known.
    private IReadOnlyList<string>? _cachedWhisparrRoots;
    private DateTime _whisparrRootsCachedAtUtc;
    // Static (process-lifetime, never disposed): the extension instance is a long-lived host singleton, and a
    // static gate avoids owning a disposable instance field (CA1001) while still serializing the cache refill.
    private static readonly SemaphoreSlim WhisparrRootsGate = new(1, 1);
    private static readonly TimeSpan WhisparrRootsCacheTtl = TimeSpan.FromMinutes(5);

    private async ValueTask<IReadOnlyList<string>> GetWhisparrRootsAsync(WhisparrClient client, CancellationToken ct)
    {
        if (_cachedWhisparrRoots is { } fresh && DateTime.UtcNow - _whisparrRootsCachedAtUtc < WhisparrRootsCacheTtl)
        {
            return fresh;
        }

        await WhisparrRootsGate.WaitAsync(ct);
        try
        {
            if (_cachedWhisparrRoots is { } cached && DateTime.UtcNow - _whisparrRootsCachedAtUtc < WhisparrRootsCacheTtl)
            {
                return cached;
            }

            // Stored creds only: the root fetch reuses the saved host/key, never a caller-supplied host.
            var (_, baseUrl, apiKey) = await ResolveCredsAsync(new TestConnectionRequest(null, null), ct);
            var result = await client.ListRootFoldersAsync(baseUrl, apiKey, ct);
            if (!result.IsOk || result.Value is not { } rows)
            {
                return []; // fail-closed: no roots → the guard rejects; cache untouched so the next event retries
            }

            var roots = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Path))
                .Select(r => r.Path!)
                .ToArray();
            _cachedWhisparrRoots = roots;
            _whisparrRootsCachedAtUtc = DateTime.UtcNow;
            return roots;
        }
        finally
        {
            WhisparrRootsGate.Release();
        }
    }

    // The per-page status surfaces (card-badge batches + toolbar summary) each need the FULL Whisparr movie set /
    // studio / performer / exclusion lists to classify a page. At thousands of items + pagination that is a huge
    // re-fetch per page — so these reads are memoized for a short TTL, shared across requests: paging costs one
    // fetch per list per window (not per page), classification stays in-memory, and a bulk action's effect shows
    // within the window. Keyed by base URL + API key so a URL OR key change invalidates within the TTL.
    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromSeconds(30);
    private readonly TtlCache<WhisparrMovie[]> _moviesCache = new(ListCacheTtl);
    private readonly TtlCache<WhisparrExclusion[]> _exclusionsCache = new(ListCacheTtl);
    private readonly TtlCache<WhisparrStudio[]> _studiosCache = new(ListCacheTtl);
    private readonly TtlCache<WhisparrPerformer[]> _performersCache = new(ListCacheTtl);
    private readonly TtlCache<WhisparrSeries[]> _seriesCache = new(ListCacheTtl);

    // Cache key: the host AND the key, so a same-host key rotation can't serve data fetched with the old key.
    private static string ListCacheKey(string baseUrl, string apiKey) => baseUrl + "\n" + apiKey;

    private Task<WhisparrResult<WhisparrMovie[]>> CachedMoviesAsync(V3Adapter adapter, string baseUrl, string apiKey, CancellationToken ct)
        => _moviesCache.GetAsync(ListCacheKey(baseUrl, apiKey), () => adapter.ListMoviesAsync(baseUrl, apiKey, ct), ct);

    private Task<WhisparrResult<WhisparrExclusion[]>> CachedExclusionsAsync(V3Adapter adapter, string baseUrl, string apiKey, CancellationToken ct)
        => _exclusionsCache.GetAsync(ListCacheKey(baseUrl, apiKey), () => adapter.ListExclusionsAsync(baseUrl, apiKey, ct), ct);

    private Task<WhisparrResult<WhisparrStudio[]>> CachedStudiosAsync(string baseUrl, string apiKey, WhisparrClient client, CancellationToken ct)
        => _studiosCache.GetAsync(ListCacheKey(baseUrl, apiKey), () => client.ListStudiosAsync(baseUrl, apiKey, ct), ct);

    private Task<WhisparrResult<WhisparrPerformer[]>> CachedPerformersAsync(string baseUrl, string apiKey, WhisparrClient client, CancellationToken ct)
        => _performersCache.GetAsync(ListCacheKey(baseUrl, apiKey), () => client.ListPerformersAsync(baseUrl, apiKey, ct), ct);

    private Task<WhisparrResult<WhisparrSeries[]>> CachedSeriesAsync(string baseUrl, string apiKey, WhisparrClient client, CancellationToken ct)
        => _seriesCache.GetAsync(ListCacheKey(baseUrl, apiKey), () => client.ListSeriesAsync(baseUrl, apiKey, ct), ct);

    // A single-slot, short-TTL memoizer for one Whisparr list read, safe under concurrent requests (one fetch
    // wins the gate; the rest read the fresh value). Only an Ok result is cached — a transient failure is not
    // sticky. `key` (the base URL) scopes the entry so switching Whisparr instances never serves stale data.
    private sealed class TtlCache<T>(TimeSpan ttl)
        where T : class
    {
        // Static (process-lifetime, never disposed): each closed generic type has exactly one cache instance, so a
        // static gate is effectively per-cache while avoiding a disposable instance field (CA1001) — as WhisparrRootsGate does.
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private string? _key;
        private T? _value;
        private DateTime _atUtc;

        public async Task<WhisparrResult<T>> GetAsync(string key, Func<Task<WhisparrResult<T>>> fetch, CancellationToken ct)
        {
            if (Fresh(key) is { } cached)
            {
                return WhisparrResult<T>.Ok(cached);
            }

            await Gate.WaitAsync(ct);
            try
            {
                if (Fresh(key) is { } racedIn)
                {
                    return WhisparrResult<T>.Ok(racedIn);
                }

                var result = await fetch();
                if (result.IsOk)
                {
                    _value = result.Value;
                    _key = key;
                    _atUtc = DateTime.UtcNow;
                }

                return result;
            }
            finally
            {
                Gate.Release();
            }
        }

        private T? Fresh(string key)
            => _value is { } value && _key == key && DateTime.UtcNow - _atUtc < ttl ? value : null;
    }
}
