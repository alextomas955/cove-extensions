using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Providers;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Missing;

// Page is counted from one. Sort, TitleSearch and the filter keys and values are opaque strings the
// provider itself issued. MenusAlreadyHeld skips the facet menu read, which costs one provider call
// per menu and answers the same between pages.
internal sealed record MissingPageRequest(
    WhisparrEntityKind Kind,
    int CoveId,
    int Page,
    int PerPage,
    string? Sort,
    string? TitleSearch,
    IReadOnlyDictionary<string, string> Filters,
    bool MenusAlreadyHeld);

internal sealed record MissingFacetSearchRequest(
    WhisparrEntityKind Kind, int CoveId, string FacetKey, string Fragment);

internal sealed record MissingPageContext(
    WhisparrBinding Binding,
    ResolvedProvider? Provider,
    IWhisparrSceneExclusionReading? ExclusionReading,
    IWhisparrEntityCatalogueReading? CatalogueReading);

// A page is never topped back up: the provider's scenes arrive, the owned ones leave, and what
// remains is what renders. Fetching more to fill the gap is unbounded where a reader owns most of
// an entity, so the range the count line states stays the provider's own rather than the card
// count.
internal sealed class MissingPagePlanner(
    MissingIdentityResolver identities,
    ProviderCatalogueSource catalogues,
    IOwnedScenePort owned,
    InstanceCatalogueCache cache)
{
    // One query's worth of identifiers. The whole catalogue is asked about, so it travels in
    // batches rather than as one parameter list whose length is the entity's catalogue.
    private const int OwnedQueryBatch = 500;

    // Named by the catalogue the stored choice points at, so reading the name is the same read as
    // choosing the catalogue.
    internal async Task<string> ProviderNameAsync(CancellationToken ct)
        => (await catalogues(ct).ConfigureAwait(false)).Capabilities.Provider;

    internal async Task<MissingPageView> PlanAsync(
        MissingPageRequest request, MissingPageContext context, ILogger log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // Chosen once for the whole plan. A page composed against two catalogues would carry one
        // source's scenes under the other's orderings.
        var catalogue = await catalogues(ct).ConfigureAwait(false);

        if (context.Provider is not { } provider)
        {
            return Refused(request, MissingRefusalKind.NoMetadataProviderConfigured, catalogue);
        }

        var identity = await identities
            .ResolveAsync(request.Kind, request.CoveId, context.Binding.Generation, ct)
            .ConfigureAwait(false);

        // A lookup that did not reach the source states nothing about which entity it holds, so it
        // refuses as unreachable rather than as no identifier.
        if (identity.ProviderEntityId is not { Length: > 0 } providerEntityId)
        {
            return Refused(
                request,
                identity.WasReached
                    ? MissingRefusalKind.NoProviderIdForEntity
                    : MissingRefusalKind.ProviderUnreachable,
                catalogue);
        }

        var listed = await CatalogueAsync(
            request.Kind, providerEntityId, context, log, ct).ConfigureAwait(false);
        if (listed.Scenes is not { } scenes)
        {
            return Refused(request, RefusalFor(listed.Refusal), catalogue);
        }

        // What the library already holds has left the missing set, and so has anything the reader
        // excluded on the instance. Both are applied over the whole catalogue rather than one page,
        // so the figure beside the tab is the number missing and not the catalogue's size.
        var missing = await MissingAmongAsync(provider.IdentityEndpoint, scenes, ct)
            .ConfigureAwait(false);
        var excluded = await ReadExcludedAsync(context, missing, ct).ConfigureAwait(false);
        var remaining = excluded.Count == 0
            ? missing
            : [.. missing.Where(scene => !excluded.Contains(scene.ProviderSceneId))];

        var page = InstanceCatalogueLogic.PageOf(
            remaining,
            request.Page,
            request.PerPage,
            request.Sort,
            request.TitleSearch,
            request.Filters);

        var menus = request.MenusAlreadyHeld
            ? []
            : InstanceCatalogueLogic.FacetsOf(remaining);

        return new MissingPageView(
            [.. page.Scenes.Select(scene => CardFor(scene, catalogue))],
            page.CatalogueSize,
            // The instance listed its whole catalogue for the entity, so the figure is a count and
            // never a floor the source would not serve past.
            false,
            request.Page,
            request.PerPage,
            page.LastPage,
            page.RangeFrom,
            page.RangeTo,
            MissingRefusalKind.None,
            [.. menus.Select(MenuFor)],
            [.. InstanceCatalogueLogic.Sorts.Select(
                sort => new MissingSortOption(sort.Value, sort.Label))],
            request.Sort is { Length: > 0 } sort ? sort : InstanceCatalogueLogic.NewestFirst,
            catalogue.Capabilities.Provider);
    }

    // The instance's own list for one entity, held briefly so the count beside the tab and the page
    // under it are one read. A transport failure is contained and reported as not read: the tab
    // states it and offers a retry.
    private async Task<WhisparrEntityCatalogue> CatalogueAsync(
        WhisparrEntityKind kind,
        string providerEntityId,
        MissingPageContext context,
        ILogger log,
        CancellationToken ct)
    {
        if (context.CatalogueReading is not { } reading)
        {
            return WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.NotReached);
        }

        if (cache.Held(context.Binding.Generation, kind, providerEntityId) is { } held)
        {
            return WhisparrEntityCatalogue.Listing(held);
        }

        WhisparrEntityCatalogue answered;
        try
        {
            answered = await reading.ReadEntityCatalogueAsync(kind, providerEntityId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            WhisparrSyncLog.SceneStatusReadContained(log, WhisparrSyncLog.Classify(failure));
            return WhisparrEntityCatalogue.Refused(WhisparrCatalogueRefusal.NotReached);
        }

        if (answered.Scenes is { } scenes)
        {
            cache.Hold(context.Binding.Generation, kind, providerEntityId, scenes);
        }

        return answered;
    }

    private static MissingRefusalKind RefusalFor(WhisparrCatalogueRefusal refusal)
        => refusal == WhisparrCatalogueRefusal.EntityNotHeld
            ? MissingRefusalKind.EntityNotInWhisparr
            : MissingRefusalKind.WhisparrCatalogueNotRead;

    // Asked in batches, so one query's parameter list is bounded by the batch and not by the
    // entity's catalogue.
    private async Task<List<WhisparrCatalogueScene>> MissingAmongAsync(
        string identityEndpoint,
        IReadOnlyList<WhisparrCatalogueScene> scenes,
        CancellationToken ct)
    {
        var inLibrary = new HashSet<string>(StringComparer.Ordinal);
        for (var at = 0; at < scenes.Count; at += OwnedQueryBatch)
        {
            var batch = scenes.Skip(at).Take(OwnedQueryBatch)
                .Select(scene => scene.ProviderSceneId)
                .ToArray();
            var held = await owned.ReadOwnedAsync(identityEndpoint, batch, ct).ConfigureAwait(false);
            foreach (var id in held)
            {
                inLibrary.Add(id);
            }
        }

        return [.. scenes.Where(scene => !inLibrary.Contains(scene.ProviderSceneId))];
    }

    // The catalogue's own size, never the number missing. Null and zero are different answers: zero
    // is a catalogue listing nothing, and null claims nothing at all.
    internal async Task<MissingCountView> CountAsync(
        MissingPageRequest request, MissingPageContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Provider is null)
        {
            return new MissingCountView(null);
        }


        var identity = await identities
            .ResolveAsync(request.Kind, request.CoveId, context.Binding.Generation, ct)
            .ConfigureAwait(false);

        if (identity.ProviderEntityId is not { Length: > 0 } providerEntityId)
        {
            return new MissingCountView(null);
        }

        // The instance's own list, minus what the library holds: the figure beside the tab is the
        // number missing rather than the size of a catalogue.
        var listed = await CatalogueAsync(
            request.Kind, providerEntityId, context, NullLogger.Instance, ct).ConfigureAwait(false);
        if (listed.Scenes is not { } scenes)
        {
            return new MissingCountView(null);
        }

        var missing = await MissingAmongAsync(context.Provider.IdentityEndpoint, scenes, ct)
            .ConfigureAwait(false);
        int? size = missing.Count;

        return new MissingCountView(size);
    }

    internal async Task<MissingFacetSearchView> SearchFacetValuesAsync(
        MissingFacetSearchRequest request, WhisparrGeneration generation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var identity = await identities
            .ResolveAsync(request.Kind, request.CoveId, generation, ct)
            .ConfigureAwait(false);

        // No identifier is no catalogue to search, not a source that lists no matching value, so
        // nothing states an absence the source never reported.
        if (identity.ProviderEntityId is not { Length: > 0 } providerEntityId)
        {
            return NoFacetValues(MissingFacetSearchOutcome.NoAnswer);
        }

        var catalogue = await catalogues(ct).ConfigureAwait(false);
        var answer = await catalogue
            .SearchFacetValuesAsync(
                request.Kind, providerEntityId, request.FacetKey, request.Fragment, ct)
            .ConfigureAwait(false);

        if (!answer.IsSearchable)
        {
            return NoFacetValues(MissingFacetSearchOutcome.NotSearchable);
        }

        return answer.Values is not { } values
            ? NoFacetValues(MissingFacetSearchOutcome.NoAnswer)
            : new MissingFacetSearchView(
                [.. values.Select(value => new MissingFacetValue(value.Value, value.Label))],
                answer.ReportedValueCount,
                MissingFacetSearchOutcome.Matched);
    }

    // The count is zero rather than a figure, because nothing was measured.
    internal static MissingFacetSearchView NoFacetValues(MissingFacetSearchOutcome outcome)
        => new([], 0, outcome);

    // A generation holding no exclusion role keeps no scene exclusions, so no request is issued.
    private static async Task<IReadOnlySet<string>> ReadExcludedAsync(
        MissingPageContext context, List<WhisparrCatalogueScene> kept, CancellationToken ct)
    {
        if (context.ExclusionReading is not { } reading || kept.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return await SceneExclusionPort
            .ReadExcludedAsync(reading, [.. kept.Select(scene => scene.ProviderSceneId)], ct)
            .ConfigureAwait(false);
    }

    // Every card came off an entry the instance holds, so its state is the row's own monitored flag
    // and never a status asked for afterwards.
    private static MissingCard CardFor(WhisparrCatalogueScene scene, IProviderCatalogue catalogue)
        => new(
            scene.ProviderSceneId,
            scene.Title,
            scene.ReleaseDate,
            scene.CoverUrl,
            catalogue.SceneAddress(scene.ProviderSceneId),
            scene.StudioName,
            scene.Description,
            [.. scene.Performers.Select(
                performer => new MissingPerformerChip(
                    performer.ForeignId, performer.Name, performer.ImageUrl))],
            scene.Tags,
            scene.Performers.Count,
            scene.Tags.Count,
            scene.Monitored ? MissingSceneState.Monitored : MissingSceneState.Unmonitored);

    private static MissingFacetMenu MenuFor(ProviderFacetMenu menu)
        => new(
            menu.Key,
            menu.Label,
            [.. menu.Values.Select(value => new MissingFacetValue(value.Value, value.Label))],
            menu.ReportedValueCount);

    // The range is empty and no ordering is in force, because no provider was asked.
    private static MissingPageView Refused(
        MissingPageRequest request, MissingRefusalKind refusal, IProviderCatalogue catalogue)
        => new(
            [],
            0,
            SizeIsLowerBound: false,
            request.Page,
            request.PerPage,
            LastPage: 1,
            RangeFrom: 0,
            RangeTo: 0,
            refusal,
            [],
            [],
            SortInForce: null,
            catalogue.Capabilities.Provider);
}
