using WhisparrSync.Client;
using WhisparrSync.Contracts;

namespace WhisparrSync.Adapters;

/// <summary>
/// The v3 adapter for an instance whose own API description declares the narrow per-entity catalogue routes:
/// everything <see cref="V3Adapter"/> does, plus the catalogue role.
/// </summary>
/// <remarks>
/// The role lives on a derived type rather than behind a flag on <see cref="V3Adapter"/> so an instance that
/// does not declare the routes yields an adapter that structurally CANNOT answer the question — a caller's
/// <c>is</c> test is the whole capability check, and there is no probe to forget to call.
/// </remarks>
internal sealed class V3EntityCatalogueAdapter(WhisparrClient client, TimeSpan? monitorSettleDelay = null)
    : V3Adapter(client, monitorSettleDelay), IWhisparrEntityCatalogue
{
    // Per-entity catalogue enumeration (read-only): ask Whisparr for the ids attributed to this entity, then
    // hydrate those ids. Issues no add/refresh/search command, so this read never mutates Whisparr.
    public async Task<WhisparrResult<EntityCatalogue>> ListEntityMoviesAsync(
        string baseUrl, string apiKey, EntityKind kind, string remoteId, CancellationToken ct)
    {
        if (kind is not (EntityKind.Studio or EntityKind.Performer))
        {
            return WhisparrResult<EntityCatalogue>.Ok(EntityCatalogue.NotEnumerable);
        }

        var idsResult = kind == EntityKind.Studio
            ? await Client.ListMovieIdsByStudioForeignIdAsync(baseUrl, apiKey, remoteId, ct)
            : await Client.ListMovieIdsByPerformerForeignIdAsync(baseUrl, apiKey, remoteId, ct);
        if (!idsResult.IsOk)
        {
            return Propagate<int[], EntityCatalogue>(idsResult);
        }

        // The upstream performer query joins Credit without DISTINCT, so a twice-credited performer could in
        // principle be named twice. The recorded seed returned one id, so this is defence at the edge rather
        // than a fix for an observed duplicate — and it costs nothing.
        var ids = idsResult.Value!.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return await ClassifyEmptyAsync(baseUrl, apiKey, kind, remoteId, ct);
        }

        // Driven by the end of the id list, never by a chunk count: stopping at a fixed number of chunks would
        // silently answer part of the entity's catalogue as though it were all of it.
        var rows = new List<WhisparrMovie>(ids.Length);
        for (var offset = 0; offset < ids.Length; offset += WhisparrClient.MaxMovieIdsPerBulkRead)
        {
            var chunk = ids[offset..Math.Min(offset + WhisparrClient.MaxMovieIdsPerBulkRead, ids.Length)];
            var hydrated = await Client.GetMoviesByIdsAsync(baseUrl, apiKey, chunk, ct);
            if (!hydrated.IsOk)
            {
                return Propagate<WhisparrMovie[], EntityCatalogue>(hydrated);
            }

            rows.AddRange(hydrated.Value!);
        }

        return WhisparrResult<EntityCatalogue>.Ok(EntityCatalogue.Known([.. rows]));
    }

    // A non-empty id list already proves Whisparr knows the entity, so this existence read is issued ONLY for
    // the empty case — where the id list alone cannot tell "known, holding nothing" from "never heard of it"
    // and a caller acting on the wrong reading would register the entity's whole catalogue.
    private async Task<WhisparrResult<EntityCatalogue>> ClassifyEmptyAsync(
        string baseUrl, string apiKey, EntityKind kind, string remoteId, CancellationToken ct)
    {
        return kind == EntityKind.Studio
            ? FromExistence(await Client.GetStudioByForeignIdAsync(baseUrl, apiKey, remoteId, ct))
            : FromExistence(await Client.GetPerformerByStashIdAsync(baseUrl, apiKey, remoteId, ct));
    }

    // The two existence reads answer different payload types and neither payload is read — only the 404-vs-200
    // classification is. Absent is checked on the ORIGINAL result: the shared propagate collapses it to
    // Unreachable, which would turn the whole discrimination this method exists for into a transport failure.
    private static WhisparrResult<EntityCatalogue> FromExistence<T>(WhisparrResult<T> existence)
        => existence.State switch
        {
            WhisparrResultState.Absent => WhisparrResult<EntityCatalogue>.Ok(EntityCatalogue.Unknown),
            WhisparrResultState.Ok => WhisparrResult<EntityCatalogue>.Ok(EntityCatalogue.Known([])),
            _ => WhisparrResult<EntityCatalogue>.PropagateFrom(existence),
        };
}
