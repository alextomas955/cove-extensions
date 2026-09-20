using Microsoft.Extensions.Logging;
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
    Uri? BaseAddress,
    string ApiKey,
    WhisparrGeneration Generation,
    ResolvedProvider? Provider,
    IWhisparrSceneStatusReading? StatusReading,
    IWhisparrSceneExclusionReading? ExclusionReading);

// A page is never topped back up: the provider's scenes arrive, the owned ones leave, and what
// remains is what renders. Fetching more to fill the gap is unbounded where a reader owns most of
// an entity, so the range the count line states stays the provider's own rather than the card
// count.
internal sealed class MissingPagePlanner(
    MissingIdentityResolver identities,
    IProviderCatalogue catalogue,
    IOwnedScenePort owned)
{
    internal string ProviderName => catalogue.Capabilities.Provider;

    internal async Task<MissingPageView> PlanAsync(
        MissingPageRequest request, MissingPageContext context, ILogger log, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Provider is not { } provider)
        {
            return Refused(request, MissingRefusalKind.NoMetadataProviderConfigured);
        }

        var identity = await identities
            .ResolveAsync(request.Kind, request.CoveId, context.Generation, ct)
            .ConfigureAwait(false);

        // A lookup that did not reach the source states nothing about which entity it holds, so it
        // refuses as unreachable rather than as no identifier.
        if (identity.ProviderEntityId is not { Length: > 0 } providerEntityId)
        {
            return Refused(
                request,
                identity.WasReached
                    ? MissingRefusalKind.NoProviderIdForEntity
                    : MissingRefusalKind.ProviderUnreachable);
        }

        // Every value the surface sent travels unchanged: the ordering and each filter value are
        // strings the provider itself issued.
        var catalogueRequest = new ProviderCatalogueRequest(
            request.Kind,
            providerEntityId,
            request.Page,
            request.PerPage,
            request.Sort,
            request.TitleSearch,
            request.Filters);

        var answer = await catalogue.ReadPageAsync(catalogueRequest, ct).ConfigureAwait(false);

        // No instance is asked about scenes that were never read.
        if (answer.Page is not { } page)
        {
            return Refused(request, MissingRefusalKind.ProviderUnreachable);
        }

        var pageIds = page.Scenes.Select(scene => scene.ProviderSceneId).ToArray();
        var held = await owned
            .ReadOwnedAsync(provider.IdentityEndpoint, pageIds, ct)
            .ConfigureAwait(false);

        var kept = page.Scenes.Where(scene => !held.Contains(scene.ProviderSceneId)).ToArray();

        // An excluded scene has left the missing set, so it is removed before any status is read.
        var excluded = await ReadExcludedAsync(context, kept, ct).ConfigureAwait(false);
        var remaining = excluded.Count == 0
            ? kept
            : [.. kept.Where(scene => !excluded.Contains(scene.ProviderSceneId))];

        var (states, statusWasRead, statusPermanentlyAbsent) = await ReadStatesAsync(
            request, context, providerEntityId, remaining, log, ct).ConfigureAwait(false);

        var menus = request.MenusAlreadyHeld
            ? []
            : await catalogue
                .ListFacetMenusAsync(request.Kind, providerEntityId, ct)
                .ConfigureAwait(false);

        return new MissingPageView(
            [.. remaining.Select(scene => CardFor(scene, states))],
            page.CatalogueSize,
            page.SizeIsLowerBound,
            request.Page,
            request.PerPage,
            page.LastPage,
            page.RangeFrom,
            page.RangeTo,
            statusWasRead ? MissingRefusalKind.None : StatusRefusal(statusPermanentlyAbsent),
            [.. menus.Select(MenuFor)],
            [.. catalogue.Sorts.Select(sort => new MissingSortOption(sort.Value, sort.Label))],
            request.Sort is { Length: > 0 } sort ? sort : catalogue.DefaultSort,
            statusWasRead,
            statusPermanentlyAbsent,
            ProviderName);
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
            .ResolveAsync(request.Kind, request.CoveId, context.Generation, ct)
            .ConfigureAwait(false);

        if (identity.ProviderEntityId is not { Length: > 0 } providerEntityId)
        {
            return new MissingCountView(null);
        }

        var size = await catalogue
            .ReadCatalogueSizeAsync(
                new ProviderCatalogueRequest(
                    request.Kind,
                    providerEntityId,
                    1,
                    request.PerPage,
                    request.Sort,
                    request.TitleSearch,
                    request.Filters),
                ct)
            .ConfigureAwait(false);

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

    private static async Task<(IReadOnlyDictionary<string, MissingSceneState> States, bool WasRead, bool PermanentlyAbsent)>
        ReadStatesAsync(
            MissingPageRequest request,
            MissingPageContext context,
            string providerEntityId,
            ProviderScene[] remaining,
            ILogger log,
            CancellationToken ct)
    {
        // A generation holding no scene-status role keeps no per-scene record at all, so no retry
        // could establish one.
        if (context.StatusReading is null)
        {
            return (Unknown(remaining), false, true);
        }

        if (context.BaseAddress is not { } baseAddress)
        {
            return (Unknown(remaining), false, false);
        }

        IReadOnlyDictionary<string, MissingSceneState> states;
        try
        {
            states = await SceneStatusPort
                .ReadStatesAsync(
                    context.StatusReading,
                    baseAddress,
                    context.ApiKey,
                    request.Kind,
                    providerEntityId,
                    [.. remaining.Select(scene => scene.ProviderSceneId)],
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            // An instance that was not reached says nothing about the catalogue, which was read, so
            // the page renders in full and states that no status was read.
            WhisparrSyncLog.SceneStatusReadContained(log, WhisparrSyncLog.Classify(failure));
            return (Unknown(remaining), false, false);
        }

        var read = states.Values.Any(state => state != MissingSceneState.StatusUnknown)
            || remaining.Length == 0;
        return (states, read, false);
    }

    // A generation holding no exclusion role keeps no scene exclusions, so no request is issued.
    private static async Task<IReadOnlySet<string>> ReadExcludedAsync(
        MissingPageContext context, ProviderScene[] kept, CancellationToken ct)
    {
        if (context.ExclusionReading is not { } reading
            || context.BaseAddress is not { } baseAddress
            || kept.Length == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return await SceneExclusionPort
            .ReadExcludedAsync(
                reading,
                baseAddress,
                context.ApiKey,
                [.. kept.Select(scene => scene.ProviderSceneId)],
                ct)
            .ConfigureAwait(false);
    }

    private static MissingRefusalKind StatusRefusal(bool permanentlyAbsent)
        => permanentlyAbsent
            ? MissingRefusalKind.WhisparrKeepsNoSceneRecords
            : MissingRefusalKind.WhisparrStatusNotRead;

    private static Dictionary<string, MissingSceneState> Unknown(
        ProviderScene[] scenes)
        => scenes.ToDictionary(
            scene => scene.ProviderSceneId,
            _ => MissingSceneState.StatusUnknown,
            StringComparer.Ordinal);

    private MissingCard CardFor(
        ProviderScene scene, IReadOnlyDictionary<string, MissingSceneState> states)
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
                    performer.ProviderPerformerId, performer.Name, performer.ImageUrl))],
            scene.Tags,
            scene.Performers.Count,
            scene.Tags.Count,
            states.GetValueOrDefault(scene.ProviderSceneId, MissingSceneState.StatusUnknown));

    private static MissingFacetMenu MenuFor(ProviderFacetMenu menu)
        => new(
            menu.Key,
            menu.Label,
            [.. menu.Values.Select(value => new MissingFacetValue(value.Value, value.Label))],
            menu.ReportedValueCount);

    // The range is empty and no ordering is in force, because no provider was asked.
    private MissingPageView Refused(MissingPageRequest request, MissingRefusalKind refusal)
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
            StatusWasRead: false,
            StatusIsPermanentlyAbsent: false,
            ProviderName);
}
