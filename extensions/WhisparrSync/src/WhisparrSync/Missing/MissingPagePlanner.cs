using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;

namespace WhisparrSync.Missing;

/// <summary>What one page of a catalogue is asked for, as the surface asked for it.</summary>
/// <param name="Kind">Which kind of entity the catalogue is for.</param>
/// <param name="CoveId">The entity in the library's own namespace.</param>
/// <param name="EntityName">The entity's name, offered to a provider that matches on one.</param>
/// <param name="Aliases">The entity's aliases, offered to a provider that matches on them.</param>
/// <param name="Page">Which page to read, counted from one.</param>
/// <param name="PerPage">How many scenes a page is read in.</param>
/// <param name="Sort">One opaque provider-issued ordering, or null for the provider's own.</param>
/// <param name="TitleSearch">A title search over the whole catalogue, or null for none.</param>
/// <param name="Filters">Facet selections, keyed by the keys the provider itself issued.</param>
/// <param name="MenusAlreadyHeld">
/// The caller already carries the facet menus, so they are not read again. A menu read is one
/// provider call per menu and the menus do not change between pages.
/// </param>
internal sealed record MissingPageRequest(
    WhisparrEntityKind Kind,
    int CoveId,
    string? EntityName,
    IReadOnlyList<string> Aliases,
    int Page,
    int PerPage,
    string? Sort,
    string? TitleSearch,
    IReadOnlyDictionary<string, string> Filters,
    bool MenusAlreadyHeld);

/// <summary>Where a page's connection and provider come from.</summary>
/// <param name="BaseAddress">The connected instance's address, or null where none is connected.</param>
/// <param name="ApiKey">The connected instance's credential.</param>
/// <param name="Generation">Which generation is connected.</param>
/// <param name="Provider">The resolved metadata provider, or null where the host names none.</param>
/// <param name="StatusReading">
/// The role that reads a scene's status, or null where the connected generation holds none.
/// </param>
/// <param name="ExclusionReading">
/// The role that reads which scenes the user excluded, or null where the connected generation holds
/// none. A generation keeping no scene records keeps no exclusions, so nothing is subtracted.
/// </param>
internal sealed record MissingPageContext(
    Uri? BaseAddress,
    string ApiKey,
    WhisparrGeneration Generation,
    ResolvedProvider? Provider,
    IWhisparrSceneStatusReading? StatusReading,
    IWhisparrSceneExclusionReading? ExclusionReading);

/// <summary>
/// Derives one page of what a provider lists and the library does not hold.
/// </summary>
/// <remarks>
/// The one place the subtraction exists. Two implementations of what is missing would disagree, and
/// the disagreement is invisible until something acts on the wrong set.
/// <para>
/// Delegate-driven and performing no I/O of its own, so a page read and a bulk run drive the same
/// derivation rather than two that can diverge.
/// </para>
/// <para>
/// A page is never topped back up. Forty scenes arrive, the owned ones leave, and what remains is
/// what renders. Fetching more to fill the gap is unbounded where a reader owns most of an entity,
/// so the range the count line states stays the provider's own rather than the card count.
/// </para>
/// </remarks>
internal sealed class MissingPagePlanner(
    MissingIdentityResolver identities,
    IProviderCatalogue catalogue,
    IOwnedScenePort owned,
    ISceneStatusPort statuses,
    ISceneExclusionPort exclusions)
{
    /// <summary>The page <paramref name="request"/> names.</summary>
    internal async Task<MissingPageView> PlanAsync(
        MissingPageRequest request, MissingPageContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Provider is not { } provider)
        {
            return Refused(request, MissingRefusalKind.NoMetadataProviderConfigured);
        }

        var providerEntityId = await identities
            .ResolveAsync(
                request.Kind,
                request.CoveId,
                context.Generation,
                request.EntityName,
                request.Aliases,
                ct)
            .ConfigureAwait(false);

        if (providerEntityId is null)
        {
            return Refused(request, MissingRefusalKind.NoProviderIdForEntity);
        }

        // Every value the surface sent travels unchanged. The ordering and each filter value are
        // strings the provider itself issued, so nothing here interprets one.
        var catalogueRequest = new ProviderCatalogueRequest(
            request.Kind,
            providerEntityId,
            request.Page,
            request.PerPage,
            request.Sort,
            request.TitleSearch,
            request.Filters);

        var page = await catalogue.ReadPageAsync(catalogueRequest, ct).ConfigureAwait(false);

        var pageIds = page.Scenes.Select(scene => scene.ProviderSceneId).ToArray();
        var held = await owned
            .ReadOwnedAsync(provider.IdentityEndpoint, pageIds, ct)
            .ConfigureAwait(false);

        var kept = page.Scenes.Where(scene => !held.Contains(scene.ProviderSceneId)).ToArray();

        // An excluded scene has left the missing set, so it is removed before any status is read and
        // no card is ever composed for it. The page is not topped back up to replace it.
        var excluded = await ReadExcludedAsync(context, kept, ct).ConfigureAwait(false);
        var remaining = excluded.Count == 0
            ? kept
            : [.. kept.Where(scene => !excluded.Contains(scene.ProviderSceneId))];

        var (states, statusWasRead, statusPermanentlyAbsent) = await ReadStatesAsync(
            request, context, providerEntityId, remaining, ct).ConfigureAwait(false);

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
            request.Sort,
            statusWasRead,
            statusPermanentlyAbsent,
            MonitorAllIsOffered: false);
    }

    /// <summary>
    /// How many scenes the provider lists for the entity <paramref name="request"/> names, or null
    /// where that could not be answered.
    /// </summary>
    /// <remarks>
    /// The catalogue's own size, which is the figure the count line states, and never the number
    /// missing. Null and zero are different answers: zero is a catalogue listing nothing, and null
    /// claims nothing at all.
    /// </remarks>
    internal async Task<MissingCountView> CountAsync(
        MissingPageRequest request, MissingPageContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Provider is null)
        {
            return new MissingCountView(null, false);
        }

        var providerEntityId = await identities
            .ResolveAsync(
                request.Kind,
                request.CoveId,
                context.Generation,
                request.EntityName,
                request.Aliases,
                ct)
            .ConfigureAwait(false);

        if (providerEntityId is null)
        {
            return new MissingCountView(null, false);
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

        return new MissingCountView(size, false);
    }

    private async Task<(IReadOnlyDictionary<string, MissingSceneState> States, bool WasRead, bool PermanentlyAbsent)>
        ReadStatesAsync(
            MissingPageRequest request,
            MissingPageContext context,
            string providerEntityId,
            ProviderScene[] remaining,
            CancellationToken ct)
    {
        // A generation holding no scene-status role keeps no per-scene record at all, so no retry
        // could establish one and the surface says so rather than offering a gesture.
        if (context.StatusReading is null)
        {
            return (Unknown(remaining), false, true);
        }

        if (context.BaseAddress is not { } baseAddress)
        {
            return (Unknown(remaining), false, false);
        }

        var states = await statuses
            .ReadStatesAsync(
                context.StatusReading,
                baseAddress,
                context.ApiKey,
                request.Kind,
                providerEntityId,
                [.. remaining.Select(scene => scene.ProviderSceneId)],
                ct)
            .ConfigureAwait(false);

        var read = states.Values.Any(state => state != MissingSceneState.StatusUnknown)
            || remaining.Length == 0;
        return (states, read, false);
    }

    // A generation holding no exclusion role keeps no scene exclusions, so there is nothing to
    // subtract and no request is issued to find that out.
    private async Task<IReadOnlySet<string>> ReadExcludedAsync(
        MissingPageContext context, ProviderScene[] kept, CancellationToken ct)
    {
        if (context.ExclusionReading is not { } reading
            || context.BaseAddress is not { } baseAddress
            || kept.Length == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return await exclusions
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

    private static MissingCard CardFor(
        ProviderScene scene, IReadOnlyDictionary<string, MissingSceneState> states)
        => new(
            scene.ProviderSceneId,
            scene.Title,
            scene.ReleaseDate,
            scene.CoverUrl,
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
            menu.IsTypeAhead);

    // A refused page states its reason and carries no scenes. The range is empty rather than a
    // provider range, because no provider was asked.
    private static MissingPageView Refused(MissingPageRequest request, MissingRefusalKind refusal)
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
            request.Sort,
            StatusWasRead: false,
            StatusIsPermanentlyAbsent: false,
            MonitorAllIsOffered: false);
}
