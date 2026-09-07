using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Discovery;

namespace WhisparrSync;

/// <summary>
/// The short-TTL memoization of the full Whisparr movie / studio / performer / series / exclusion list reads
/// the per-page status surfaces (card-badge batches + toolbar summaries) classify against. Shared across the
/// monitor + scene-status slices so paging a large library costs one fetch per list per window, not one per
/// page. Keyed by base URL + API key so a URL OR key change invalidates within the TTL.
/// </summary>
public sealed partial class WhisparrSync
{
    private static readonly TimeSpan ListCacheTtl = TimeSpan.FromSeconds(30);
    private readonly TtlCache<WhisparrMovie[]> _moviesCache = new(ListCacheTtl);
    private readonly TtlCache<WhisparrExclusion[]> _exclusionsCache = new(ListCacheTtl);
    private readonly TtlCache<WhisparrStudio[]> _studiosCache = new(ListCacheTtl);
    private readonly TtlCache<WhisparrPerformer[]> _performersCache = new(ListCacheTtl);
    private readonly TtlCache<WhisparrSeries[]> _seriesCache = new(ListCacheTtl);

    // The per-entity direct discovery catalogue (a direct metadata box — StashDB on v3 or ThePornDB on v2, not
    // Whisparr) — single-slot, so the count + entity reads and paging/refresh of the SAME entity within the
    // window share one fetch; switching entities re-fetches. Keyed by the connected version + box endpoint + box
    // key + entity, so a version switch, a credential/box switch, OR a different entity invalidates. One slot
    // serves both boxes: a TPDB endpoint/key never collides with a StashDB one. Transient, never persisted.
    private readonly TtlCache<DiscoveryCatalogue> _directCatalogueCache = new(ListCacheTtl);

    // The paged direct read's slot: a different SHAPE from _directCatalogueCache (a single page + its total +
    // hasMore, not the whole catalogue), so it gets its own slot. Keyed by the connected version + box endpoint +
    // box key + entity + PAGE + the QUERY, so requesting page 2 never serves page 1's cached slice and a
    // title-ordered request never serves a date-ordered page. The slot holds ONE entry, so changing the ordering
    // or a filter thrashes it and costs one provider read (~900 ms measured) — a stated cost, not a defect.
    // Transient, never persisted.
    private readonly TtlCache<DiscoveryPage> _directCataloguePageCache = new(ListCacheTtl);

    // The per-entity facet aggregate's slot: a different SHAPE again from a catalogue page — option lists
    // describing the ENTITY, not the rows of the current view. Its key omits BOTH the page and the query, since a
    // studio's performer roster does not change because the reader re-sorted or narrowed what they are looking at.
    // That omission is the whole cost bound: the aggregate is paid once per entity open per window and never per
    // page. Only an Ok result caches, leaving a transient aggregate failure un-sticky. Transient, never persisted.
    private readonly TtlCache<DiscoveryFacetOptions> _directFacetAggregateCache = new(ListCacheTtl);

    // The connection's capability answer, read from the instance's own API description. Its own slot because it
    // is a different SHAPE from every list above — one decision about the instance, not rows. Only an Ok result
    // caches, so a transient document failure is retried on the next read rather than sticking as "absent".
    private readonly TtlCache<WhisparrCapabilities> _capabilityCache = new(ListCacheTtl);

    // The long-lived capability port for this extension instance, so one sync run resolves the answer once and
    // the memo is not re-created per unit.
    private WhisparrCapabilityPort CapabilityPort(WhisparrClient client)
        => new(client, (key, fetch, ct) => _capabilityCache.GetAsync(key, fetch, ct));

    // The connected version LEADS the key, then the host AND the API key. The version is there for the reason
    // DiscoveryCacheKeys records: the connection-scoped clear does not see a within-connection v3↔v2 switch on
    // the same URL and key, so a version-less key serves the prior generation's slice. The host and key are
    // there so a same-host key rotation cannot serve data fetched with the old one. Stated once, here — the
    // five slots that compose it stay silent.
    internal static string ListCacheKey(string version, string baseUrl, string apiKey)
        => version + "\n" + baseUrl + "\n" + apiKey;

    // The connected version + box endpoint + key is part of the cache key so a version switch OR a credential/box
    // switch cannot serve stale data.
    private Task<WhisparrResult<DiscoveryCatalogue>> CachedDirectCatalogueAsync(
        IDiscoverySource provider, EntityKind kind, IReadOnlyList<string> remoteIds, string version, string endpoint, string apiKey, CancellationToken ct)
        => _directCatalogueCache.GetAsync(
            DiscoveryCacheKeys.For(version, endpoint, apiKey, kind, remoteIds, page: null, DiscoveryQuery.Default),
            () => provider.EnumerateCatalogueAsync(kind, remoteIds, ct),
            ct);

    // The paged read. Its key holds version + box endpoint + key + entity + PAGE + the QUERY: a rapid double-mount
    // of the same page then shares one fetch, while a later page, a different ordering, a different filter and a
    // version switch each do their own read. An omitted dimension is how an earlier page's or another query's
    // cached slice gets served.
    private Task<WhisparrResult<DiscoveryPage>> CachedDirectCataloguePageAsync(
        IDiscoverySource provider, EntityKind kind, IReadOnlyList<string> remoteIds, string version, int page, int perPage,
        string endpoint, string apiKey, DiscoveryQuery query, CancellationToken ct)
        => _directCataloguePageCache.GetAsync(
            DiscoveryCacheKeys.For(version, endpoint, apiKey, kind, remoteIds, page, query),
            () => provider.EnumerateCataloguePageAsync(kind, remoteIds, page, perPage, query, ct),
            ct);

    // The memo a direct source reads its whole-set aggregates through. The key composes the same dimensions the
    // catalogue key does MINUS the page and the query. A non-Ok aggregate is reported once here and handed back
    // as it came; the source then keeps the page's own option values and leaves the axis unclaimed. The outcome
    // therefore reaches the reader as a label at the control, and not only as a log line.
    private async Task<WhisparrResult<DiscoveryFacetOptions>> CachedDirectFacetAggregateAsync(
        EntityKind kind, IReadOnlyList<string> remoteIds, string version, string endpoint, string apiKey,
        Func<Task<WhisparrResult<DiscoveryFacetOptions>>> fetch, CancellationToken ct)
    {
        var result = await _directFacetAggregateCache.GetAsync(
            DiscoveryCacheKeys.For(version, endpoint, apiKey, kind, remoteIds, page: null, DiscoveryQuery.Default),
            fetch, ct);
        if (!result.IsOk)
        {
            LogFacetAggregateUnavailable(result.State.ToString());
        }

        return result;
    }

    // Typed on V3Adapter because every consumer of the memo is a v3 path today. The memo is worth MORE on v2,
    // which synthesizes the same movie set from series → episode at 1 + 2N calls, so a v2 consumer arriving is
    // the signal to widen this back to the reconcile role rather than to add a second cache.
    //
    // Two consumers remain: the per-card batch classify and the discovery status index. Both need members the
    // narrow status projection does not carry, so neither can move to it as it stands. REMOVAL CONDITION: this
    // slot goes when both are narrowed, and not before — it is what amortises the whole-set read across them
    // today, so dropping it first would turn one read per window into one read per page.
    private Task<WhisparrResult<WhisparrMovie[]>> CachedMoviesAsync(V3Adapter adapter, string version, string baseUrl, string apiKey, CancellationToken ct)
        => _moviesCache.GetAsync(ListCacheKey(version, baseUrl, apiKey), () => adapter.ListMoviesAsync(baseUrl, apiKey, ct), ct);

    // Exclusions stay v3-only: v2 exclusions are TPDB-keyed and this build reads none, and a widened parameter
    // would advertise a capability the older generation does not answer.
    private Task<WhisparrResult<WhisparrExclusion[]>> CachedExclusionsAsync(V3Adapter adapter, string version, string baseUrl, string apiKey, CancellationToken ct)
        => _exclusionsCache.GetAsync(ListCacheKey(version, baseUrl, apiKey), () => adapter.ListExclusionsAsync(baseUrl, apiKey, ct), ct);

    private Task<WhisparrResult<WhisparrStudio[]>> CachedStudiosAsync(string version, string baseUrl, string apiKey, WhisparrClient client, CancellationToken ct)
        => _studiosCache.GetAsync(ListCacheKey(version, baseUrl, apiKey), () => client.ListStudiosAsync(baseUrl, apiKey, ct), ct);

    private Task<WhisparrResult<WhisparrPerformer[]>> CachedPerformersAsync(string version, string baseUrl, string apiKey, WhisparrClient client, CancellationToken ct)
        => _performersCache.GetAsync(ListCacheKey(version, baseUrl, apiKey), () => client.ListPerformersAsync(baseUrl, apiKey, ct), ct);

    private Task<WhisparrResult<WhisparrSeries[]>> CachedSeriesAsync(string version, string baseUrl, string apiKey, WhisparrClient client, CancellationToken ct)
        => _seriesCache.GetAsync(ListCacheKey(version, baseUrl, apiKey), () => client.ListSeriesAsync(baseUrl, apiKey, ct), ct);
}

/// <summary>
/// Composes the discovery catalogue cache key. The connected version LEADS the key so a within-connection
/// version switch (v3↔v2 on the same URL + key, which the connection-scoped clear does not see) can never serve
/// the prior version's cached slice; the box endpoint + key scope it to the credential it was read with, the
/// entity narrows it, the optional page makes each page its own slot (page 2 never serves page 1's rows), and the
/// query makes each ordering and each facet narrowing its own slot.
/// </summary>
internal static class DiscoveryCacheKeys
{
    // A placeholder for an unset query member. It cannot collide with a real value: a filter id that reached here
    // passed a UUID or digits-only shape check, and a year is an integer.
    private const string Unset = "-";

    public static string For(
        string version, string endpoint, string apiKey, EntityKind kind, IReadOnlyList<string> remoteIds, int? page,
        DiscoveryQuery query)
    {
        // The ids are ordered before joining so a parent's union slot is stable regardless of the DB row order,
        // and a parent's multi-id slot never collides with the bare-parent single-id slot.
        var ids = string.Join(",", remoteIds.OrderBy(id => id, StringComparer.Ordinal));
        var key = version + "\n" + endpoint + "\n" + apiKey + "\n" + kind + "\n" + ids;
        var paged = page is { } p ? key + "\n" + p : key;
        return paged + "\n" + Canonical(query);
    }

    // EVERY query member renders, in a fixed order, with a placeholder for an unset one — two equal queries
    // therefore always render one string, and two queries differing in any single dimension always render two.
    // Omitting a member here is how a filtered page gets served to an unfiltered request.
    private static string Canonical(DiscoveryQuery query)
        => string.Join(
            "\n",
            query.Sort.ToString(),
            query.StudioFilterId ?? Unset,
            query.PerformerFilterId ?? Unset,
            query.TagFilterId ?? Unset,
            query.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Unset);
}

/// <summary>
/// A single-slot, short-TTL memoizer for one Whisparr list read, safe under concurrent requests (one fetch
/// wins the gate; the rest read the fresh value). Only an Ok result is cached — a transient failure is not
/// sticky. <paramref name="ttl"/> bounds staleness; the <c>key</c> (base URL + key) scopes the entry so
/// switching Whisparr instances never serves stale data.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Reliability", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The gate is a process-lifetime memoizer field, intentionally never disposed (the cache " +
        "lives for the app's lifetime); an IDisposable contract would add churn for no gain.")]
internal sealed class TtlCache<T>(TimeSpan ttl)
    where T : class
{
    // Per-instance, not static: a shared static gate would cross-serialize refills of unrelated TtlCache<T> of the same T.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _key;
    private T? _value;
    private DateTime _atUtc;

    public async Task<WhisparrResult<T>> GetAsync(string key, Func<Task<WhisparrResult<T>>> fetch, CancellationToken ct)
    {
        if (Fresh(key) is { } cached)
        {
            return WhisparrResult<T>.Ok(cached);
        }

        await _gate.WaitAsync(ct);
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
            _gate.Release();
        }
    }

    /// <summary>Drops the entry so the next read refills.</summary>
    /// <remarks>
    /// Taken under the same gate a refill holds, so a refill already in flight cannot write its pre-clear value
    /// back afterwards and resurrect the entry this call exists to drop.
    /// </remarks>
    public async Task ClearAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _key = null;
            _value = null;
            _atUtc = default;
        }
        finally
        {
            _gate.Release();
        }
    }

    private T? Fresh(string key)
        => _value is { } value && _key == key && DateTime.UtcNow - _atUtc < ttl ? value : null;
}
