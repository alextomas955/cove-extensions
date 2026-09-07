using System.Globalization;
using Microsoft.AspNetCore.Http;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync;

/// <summary>
/// The studios/performers status projection: classifies a page of Cove entity ids into their Whisparr
/// monitored/present counts, and the library-wide monitored summary. This is the ONE place the residual
/// v2/v3 fetch+classify split lives (v2 keys sites by ThePornDB against the series list; v3 keys studios/
/// performers by StashDB) — kept OUT of the monitor <c>*Endpoints.cs</c> so the endpoint file gates purely on
/// role-interface presence. Both handlers are configure-gated + stored-creds-only.
/// </summary>
public sealed partial class WhisparrSync
{
    /// <summary>
    /// Studio/performer status for the per-card badges — a studios/performers page costs ONE Whisparr fetch
    /// (the entity list), not one per card. Configure-gated, stored creds only, v3-only (a v2 instance
    /// returns <c>VERSION_UNSUPPORTED</c>, and the entity card slots are not registered on v2 anyway). An
    /// unresolvable id, or one absent from Whisparr, is simply missing from the map (the card shows no badge).
    /// </summary>
    internal async Task<IResult> EntityStatusBatchAsync(
        EntityStatusBatchRequest req, WhisparrClient client, CancellationToken ct)
    {
        if (!TryParseEntityKind(req.Kind, out var kind))
        {
            return Results.Json(new ErrorResponse("UNKNOWN_KIND"), statusCode: 400);
        }

        var coveIds = req.CoveEntityIds ?? [];
        if (coveIds.Length > MaxEntityIdsPerRequest)
        {
            return Results.Json(new TooManyIdsResponse("TOO_MANY_IDS", MaxEntityIdsPerRequest), statusCode: 400);
        }

        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        var adapter = AdapterSelector.SelectForVersion(options.SelectedVersion, client);
        if (adapter is null || (kind != EntityKind.Studio && adapter is not IWhisparrPerformerMonitor))
        {
            return VersionUnsupported();
        }

        if (coveIds.Length == 0)
        {
            return Results.Json(new EntityStatusBatchResponse(new Dictionary<int, EntityStatus>()), EnumStringResponseJsonOptions);
        }

        return await WithScopedLibraryAsync(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
        {
            // Cached entity-list read: paging re-uses one fetch per TTL window, not one per page. A failed
            // read degrades to empty (no badges), never a thrown page. No movie set — the count is Whisparr's own.
            IReadOnlyDictionary<int, EntityStatus> states;
            if (adapter is V2Adapter)
            {
                // v2: studio-only (guarded above), matched by TPDB against the site (series) list.
                var series = await CachedSeriesAsync(options.SelectedVersion ?? string.Empty, baseUrl, apiKey, client, ct);
                states = await V2EntityStatusBatchCoreAsync(
                    coveIds, library, series is { IsOk: true } ? series.Value! : [], ct);
            }
            else
            {
                var studios = kind == EntityKind.Studio ? await CachedStudiosAsync(options.SelectedVersion ?? string.Empty, baseUrl, apiKey, client, ct) : null;
                var performers = kind == EntityKind.Performer ? await CachedPerformersAsync(options.SelectedVersion ?? string.Empty, baseUrl, apiKey, client, ct) : null;
                states = await EntityStatusBatchCoreAsync(
                    kind, coveIds, library,
                    studios is { IsOk: true } ? studios.Value! : [],
                    performers is { IsOk: true } ? performers.Value! : [],
                    ct);
            }

            return Results.Json(new EntityStatusBatchResponse(states), EnumStringResponseJsonOptions);
        });
    }

    /// <summary>
    /// The classify half of <see cref="EntityStatusBatchAsync"/> (pure over the pre-fetched entity list, so it is
    /// unit-testable host-free and re-uses the cached list). Resolves each Cove id to its StashDB id via the
    /// library, then <see cref="V3Adapter.ClassifyEntityStatusBatch"/> yields every entity's monitored +
    /// scenesPresent/scenesTotal. An id with no StashDB id, or no Whisparr entity, is absent from the map.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<int, EntityStatus>> EntityStatusBatchCoreAsync(
        EntityKind kind, IReadOnlyList<int> coveEntityIds, ICoveLibraryPort library,
        WhisparrStudio[] studios, WhisparrPerformer[] performers, CancellationToken ct)
    {
        var coveToStash = new Dictionary<int, string>();
        foreach (var id in coveEntityIds)
        {
            var identity = await library.LoadEntityIdentityAsync(kind, id, ct);
            if (identity?.StashIds.FirstOrDefault(x => !string.IsNullOrEmpty(x)) is { } stashId)
            {
                coveToStash[id] = stashId;
            }
        }

        var result = new Dictionary<int, EntityStatus>();
        if (coveToStash.Count == 0)
        {
            return result;
        }

        var byStash = V3Adapter.ClassifyEntityStatusBatch(
            kind, [.. coveToStash.Values.Distinct(StringComparer.OrdinalIgnoreCase)], studios, performers);
        foreach (var (coveId, stashId) in coveToStash)
        {
            if (byStash.TryGetValue(stashId, out var status))
            {
                result[coveId] = status;
            }
        }

        return result;
    }

    /// <summary>
    /// The v2 studio half of <see cref="EntityStatusBatchAsync"/>: resolves each Cove studio to its ThePornDB id
    /// (v2's identity key, not StashDB) and classifies against the pre-fetched site (series) list via
    /// <see cref="V2Adapter.ClassifyStudioStatusBatch"/>. Studio-only — v2 has no performer entity.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<int, EntityStatus>> V2EntityStatusBatchCoreAsync(
        IReadOnlyList<int> coveEntityIds, ICoveLibraryPort library, WhisparrSeries[] series, CancellationToken ct)
    {
        var coveToTpdb = new Dictionary<int, string>();
        foreach (var id in coveEntityIds)
        {
            var identity = await library.LoadEntityIdentityAsync(EntityKind.Studio, id, ct);
            if (identity?.TpdbIds.FirstOrDefault(x => !string.IsNullOrEmpty(x)) is { } tpdbId)
            {
                coveToTpdb[id] = tpdbId;
            }
        }

        var result = new Dictionary<int, EntityStatus>();
        if (coveToTpdb.Count == 0)
        {
            return result;
        }

        var byTpdb = V2Adapter.ClassifyStudioStatusBatch(
            [.. coveToTpdb.Values.Distinct(StringComparer.OrdinalIgnoreCase)], series);
        foreach (var (coveId, tpdbId) in coveToTpdb)
        {
            if (byTpdb.TryGetValue(tpdbId, out var status))
            {
                result[coveId] = status;
            }
        }

        return result;
    }

    /// <summary>
    /// The library-wide monitored summary for the studios/performers toolbar row: how many Cove entities of the
    /// kind are monitored in Whisparr, over the total in the library. Enumerates every Cove entity of the kind
    /// ONCE (id + StashDB ids) and matches against the cached Whisparr entity list — one entity-list fetch per TTL
    /// window, so it stays cheap for a library with thousands of studios/performers. An entity with no StashDB id,
    /// or absent from Whisparr, counts toward the total but never <c>monitored</c>.
    /// </summary>
    internal static async Task<EntityLibrarySummary> EntityLibrarySummaryCoreAsync(
        EntityKind kind, ICoveLibraryPort library,
        WhisparrStudio[] studios, WhisparrPerformer[] performers, CancellationToken ct)
    {
        var identities = await library.LoadAllEntityIdentitiesAsync(kind, ct);
        if (identities.Count == 0)
        {
            return new EntityLibrarySummary(Total: 0, Monitored: 0);
        }

        var monitoredForeignIds = kind == EntityKind.Studio
            ? studios.Where(s => s.Monitored).Select(s => s.ForeignId)
            : performers.Where(p => p.Monitored).Select(p => p.ForeignId);
        var monitored = new HashSet<string>(
            monitoredForeignIds.Where(id => !string.IsNullOrEmpty(id))!, StringComparer.OrdinalIgnoreCase);

        var monitoredCount = identities.Count(
            identity => identity.StashIds.Any(id => !string.IsNullOrEmpty(id) && monitored.Contains(id)));
        return new EntityLibrarySummary(Total: identities.Count, Monitored: monitoredCount);
    }

    /// <summary>
    /// The v2 studio variant of <see cref="EntityLibrarySummaryCoreAsync"/>: counts monitored sites over every
    /// Cove studio, matching each studio's ThePornDB id against the monitored series' <c>tvdbId</c>. Studio-only.
    /// </summary>
    internal static async Task<EntityLibrarySummary> V2EntityLibrarySummaryCoreAsync(
        ICoveLibraryPort library, WhisparrSeries[] series, CancellationToken ct)
    {
        var identities = await library.LoadAllEntityIdentitiesAsync(EntityKind.Studio, ct);
        if (identities.Count == 0)
        {
            return new EntityLibrarySummary(Total: 0, Monitored: 0);
        }

        var monitored = new HashSet<string>(
            series.Where(s => s.Monitored && s.TvdbId is not null)
                .Select(s => s.TvdbId!.Value.ToString(CultureInfo.InvariantCulture)),
            StringComparer.OrdinalIgnoreCase);

        var monitoredCount = identities.Count(
            identity => identity.TpdbIds.Any(id => !string.IsNullOrEmpty(id) && monitored.Contains(id)));
        return new EntityLibrarySummary(Total: identities.Count, Monitored: monitoredCount);
    }

    /// <summary>
    /// The studios/performers toolbar row's library-wide monitored count. Configure-gated, stored creds only.
    /// Studios work on both versions (v2 matches by ThePornDB against the site list); performers are v3-only, so a
    /// v2 performer returns <c>VERSION_UNSUPPORTED</c>. <c>available</c> is false when the Whisparr entity-list
    /// read failed, so the client shows "Whisparr unavailable" rather than a misleading "0 monitored".
    /// </summary>
    internal async Task<IResult> EntityLibrarySummaryAsync(
        string? kind, WhisparrClient client, CancellationToken ct)
    {
        if (!TryParseEntityKind(kind, out var entityKind))
        {
            return Results.Json(new ErrorResponse("UNKNOWN_KIND"), statusCode: 400);
        }

        var (options, baseUrl, apiKey) = await StoredCredsAsync(ct);
        var adapter = AdapterSelector.SelectForVersion(options.SelectedVersion, client);
        if (adapter is null || (entityKind != EntityKind.Studio && adapter is not IWhisparrPerformerMonitor))
        {
            return VersionUnsupported();
        }

        return await WithScopedLibraryAsync(options.StashDbEndpoint, options.TpdbEndpoint, async library =>
        {
            if (adapter is V2Adapter)
            {
                var series = await CachedSeriesAsync(options.SelectedVersion ?? string.Empty, baseUrl, apiKey, client, ct);
                var v2Summary = await V2EntityLibrarySummaryCoreAsync(
                    library, series is { IsOk: true } ? series.Value! : [], ct);
                return Results.Json(
                    new EntityLibrarySummaryResponse(series is { IsOk: true }, v2Summary.Total, v2Summary.Monitored),
                    EnumStringResponseJsonOptions);
            }

            var studios = entityKind == EntityKind.Studio ? await CachedStudiosAsync(options.SelectedVersion ?? string.Empty, baseUrl, apiKey, client, ct) : null;
            var performers = entityKind == EntityKind.Performer ? await CachedPerformersAsync(options.SelectedVersion ?? string.Empty, baseUrl, apiKey, client, ct) : null;
            var available = entityKind == EntityKind.Studio ? studios is { IsOk: true } : performers is { IsOk: true };
            var summary = await EntityLibrarySummaryCoreAsync(
                entityKind, library,
                studios is { IsOk: true } ? studios.Value! : [],
                performers is { IsOk: true } ? performers.Value! : [],
                ct);
            return Results.Json(
                new { available, total = summary.Total, monitored = summary.Monitored }, EnumStringResponseJsonOptions);
        });
    }
}
