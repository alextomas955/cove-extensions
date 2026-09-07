using System.Collections.Frozen;
using WhisparrSync.Client;
using WhisparrSync.Contracts;

namespace WhisparrSync.Discovery;

/// <summary>
/// A card field the source contract governs: a source populates it on its mapped <see cref="WhisparrMovie"/> or
/// declares it in <see cref="IDiscoverySource.UnavailableFields"/> — the conformance guard fails one that does
/// neither. Whisparr status is intentionally NOT here: the status projector derives it after the source.
/// </summary>
internal enum DiscoveryField
{
    Title,
    ReleaseDate,
    StudioName,
    CoverUrl,
    PerformerName,
    PerformerImageUrl,
    Tags,
    Overview,
}

/// <summary>
/// A set-preserving ordering a discovery read can ask its source for. The three members mirror the shipped sort
/// controls, and each maps to a provider ordering that returns the same SET in a different order.
/// </summary>
/// <remarks>
/// StashDB's <c>TRENDING</c> and <c>POPULARITY</c> are deliberately unrepresentable here. Measured live, a studio
/// whose catalogue reported 390 scenes under a date ordering reported 216 under trending: those values are a
/// ranked WINDOW over the set, not an ordering of it, and a fourth member would let one reach the wire.
/// </remarks>
internal enum DiscoverySortMode
{
    Newest,
    Oldest,
    Title,
}

/// <summary>
/// A cross-cutting axis a discovery read can ask its source to narrow on, mirroring the shipped facet controls.
/// The entity's own axis is never one of these — it is already the read's subject.
/// </summary>
internal enum DiscoveryFacetAxis
{
    Studio,
    Performer,
    Tag,
    Year,
}

/// <summary>
/// What a discovery read asks of its source beyond the entity itself: one ordering plus the facet narrowings.
/// Every member defaults to the shipped read, which <see cref="Default"/> names.
/// </summary>
/// <remarks>
/// The filter ids are the PROVIDER's own (a StashDB UUID on v3, a ThePornDB numeric id on v2), shape-validated at
/// the endpoint by <see cref="DiscoveryQueryGuard"/> before they reach a source. A source applies only the
/// dimensions it declares in <see cref="IDiscoverySource.ServerSideSorts"/> /
/// <see cref="IDiscoverySource.ServerSideFacets"/>; an undeclared dimension is ignored, and the declaration
/// travels back on the response for the caller to word honestly.
/// </remarks>
internal sealed record DiscoveryQuery(
    DiscoverySortMode Sort = DiscoverySortMode.Newest,
    string? StudioFilterId = null,
    string? PerformerFilterId = null,
    string? TagFilterId = null,
    int? Year = null)
{
    /// <summary>The shipped read: newest first, no facet narrowing.</summary>
    internal static readonly DiscoveryQuery Default = new();
}

/// <summary>
/// A pluggable discovery provider: enumerates an entity's full metadata-source catalogue so the pure
/// <see cref="DiscoveryService"/> can diff it against what Cove owns. The abstraction is provider-agnostic — the
/// difference engine runs over source ids independent of the provider. The providers are the direct Cove-credential
/// metadata sources: StashDB on v3 and ThePornDB on v2, resolved from Cove's own configured metadata server.
/// </summary>
internal interface IDiscoverySource
{
    /// <summary>
    /// The <see cref="DiscoveryField"/>s this source genuinely cannot supply. Empty (the default) means it supplies
    /// every field — capability-by-presence, not a per-field probe. A source with a real gap overrides this to name
    /// exactly those fields; the conformance guard then exempts them from its populate check.
    /// </summary>
    IReadOnlySet<DiscoveryField> UnavailableFields => FrozenSet<DiscoveryField>.Empty;

    /// <summary>
    /// The <see cref="DiscoverySortMode"/>s this source applies SERVER-SIDE, over the whole catalogue. Empty (the
    /// default) means it applies NONE: an undeclared source degrades to whatever ordering the caller manages over
    /// the rows it loaded, and never claims the provider ordered the whole set. Capability-by-declaration,
    /// mirroring <see cref="UnavailableFields"/> with the default direction inverted; never a probe.
    /// </summary>
    IReadOnlySet<DiscoverySortMode> ServerSideSorts => FrozenSet<DiscoverySortMode>.Empty;

    /// <summary>
    /// The <see cref="DiscoveryFacetAxis"/>es this source applies SERVER-SIDE, over the whole catalogue. Empty (the
    /// default) means it applies NONE, and the caller narrows the loaded rows itself. Same declaration-never-probe
    /// rule as <see cref="ServerSideSorts"/>.
    /// </summary>
    IReadOnlySet<DiscoveryFacetAxis> ServerSideFacets => FrozenSet<DiscoveryFacetAxis>.Empty;

    /// <summary>
    /// The <see cref="DiscoveryFacetAxis"/>es this source can enumerate the WHOLE SET's option values for on a page
    /// of the given <paramref name="kind"/> — an aggregate over the catalogue, distinct from the values one page
    /// happens to carry. Empty (the default) means none, and the caller derives that axis's options from the
    /// rendered rows. The answer is per kind because a provider aggregate exists for some entity kinds and not
    /// others.
    /// </summary>
    /// <remarks>
    /// A CAPABILITY, answered without reading anything. Whether a particular read actually delivered a whole-set
    /// list is a property of that read and rides on <see cref="DiscoveryPage.WholeSetAxes"/>: an aggregate can be
    /// unreachable, can come back empty, or can describe only part of an entity whose catalogue unions several
    /// provider ids. A caller wording a control reads the PAGE; a caller describing the source reads this.
    /// </remarks>
    IReadOnlySet<DiscoveryFacetAxis> WholeSetFacetAxes(EntityKind kind) => FrozenSet<DiscoveryFacetAxis>.Empty;

    /// <summary>
    /// Resolves an entity's source id from its NAME, for a Cove entity that stores no remote id for this provider.
    /// Null when the source has no single confident match; the caller then reports its honest no-id state.
    /// </summary>
    /// <remarks>
    /// A FALLBACK only — a stored remote id always wins. The default is "no name lookup": a provider that cannot
    /// resolve by name leaves it unoverridden (capability-by-presence, no probe).
    /// </remarks>
    Task<WhisparrResult<string?>> ResolveIdByNameAsync(EntityKind kind, string name, CancellationToken ct)
        => Task.FromResult(WhisparrResult<string?>.Ok(null));

    /// <summary>
    /// Whether the provider needs its OWN credential. The direct StashDB/TPDB providers resolve a Cove metadata
    /// credential, so they set this true.
    /// </summary>
    bool NeedsCredential { get; }

    /// <summary>
    /// Whether the provider can enumerate an UNMONITORED entity's catalogue. The direct StashDB/TPDB providers
    /// read the full metadata-source catalogue independent of Whisparr, so they can.
    /// </summary>
    bool CanEnumerateUnmonitored { get; }

    /// <summary>
    /// Enumerates the entity's full source catalogue by its server-resolved <paramref name="remoteIds"/> — a
    /// read-only enumeration that issues no add/refresh/search command. A parent studio carries its own id plus
    /// every child sub-studio's id so one read unions the network; every other entity carries a single id.
    /// A source that cannot union (TPDB) reads the first id (the entity's own). An entity the source does not
    /// know returns an empty set; a non-Ok upstream read propagates its classified state.
    /// </summary>
    Task<WhisparrResult<DiscoveryCatalogue>> EnumerateCatalogueAsync(
        EntityKind kind, IReadOnlyList<string> remoteIds, CancellationToken ct);

    /// <summary>
    /// Enumerates ONE page of the entity's source catalogue at the 1-based <paramref name="page"/> /
    /// <paramref name="perPage"/> — the read-path counterpart of <see cref="EnumerateCatalogueAsync"/> the
    /// incremental Missing tab loads a page at a time. The default delegates to the full enumeration and returns
    /// it as a single page (<c>hasMore:false</c>), so a provider that does not page its source still answers; a
    /// provider whose source paginates natively (StashDB) overrides this to fetch exactly one upstream page.
    /// Read-only, same classify-not-throw contract as the full enumeration.
    /// <paramref name="query"/> carries the ordering and facet narrowing asked of the source; the default
    /// implementation IGNORES it, which is honest for a source that declares no server-side axis.
    /// </summary>
    async Task<WhisparrResult<DiscoveryPage>> EnumerateCataloguePageAsync(
        EntityKind kind, IReadOnlyList<string> remoteIds, int page, int perPage, DiscoveryQuery query,
        CancellationToken ct)
    {
        var all = await EnumerateCatalogueAsync(kind, remoteIds, ct);
        return all.IsOk
            ? WhisparrResult<DiscoveryPage>.Ok(
                new DiscoveryPage(
                    all.Value!.Movies, all.Value!.Movies.Length, false,
                    CatalogueTruncated: all.Value!.Truncated))
            : WhisparrResult<DiscoveryPage>.PropagateFrom(all);
    }
}

/// <summary>
/// One page of an entity's source catalogue: the page's <see cref="Movies"/> (already mapped to the diff-ready
/// <see cref="WhisparrMovie"/> shape), the catalogue <see cref="Total"/> when the source advertises one (else
/// null), and <see cref="HasMore"/> — whether a further page remains.
/// </summary>
/// <remarks>
/// <c>TotalIsAtLeast</c> marks <c>Total</c> as a LOWER BOUND, for a source that saturates its reported total
/// (ThePornDB stops counting at 10,000). A caller renders that as "10,000+", never as an exact size.
/// <c>FacetOptions</c> carries each axis's selectable values, whichever end they came from.
/// <para>
/// <c>WholeSetAxes</c> is what separates the two ends: it names the axes whose <c>FacetOptions</c> list came from
/// a provider aggregate over the whole catalogue ON THIS READ. Every other axis's list is the values these rows
/// carry, and a caller must word its control as such. Null means none — the safe reading in both directions,
/// since an absent claim degrades to the honest one.
/// </para>
/// </remarks>
internal sealed record DiscoveryPage(
    WhisparrMovie[] Movies,
    int? Total,
    bool HasMore,
    bool TotalIsAtLeast = false,
    DiscoveryFacetOptions? FacetOptions = null,
    IReadOnlySet<DiscoveryFacetAxis>? WholeSetAxes = null,
    bool CatalogueTruncated = false);

/// <summary>An entity's whole source catalogue, and whether a page ceiling cut the read short.</summary>
/// <remarks>
/// <c>Truncated</c> exists because the rows alone cannot carry it. A provider that pages to a hard ceiling
/// returns a partial catalogue that is shape-identical to a complete one, and the Missing diff is
/// <c>catalogue − owned − excluded</c> — so a silently capped read UNDER-reports missing scenes for exactly
/// the largest entities, where the answer matters most. Raising the ceiling would only move that point;
/// the discriminator is the remedy. <c>ReconcileJob</c> is the in-repo model of a cap done right.
/// </remarks>
internal sealed record DiscoveryCatalogue(WhisparrMovie[] Movies, bool Truncated);

/// <summary>
/// The caller-owned memo a source reads a whole-set aggregate through: the source supplies the fetch, the caller
/// supplies the slot it is remembered in.
/// </summary>
/// <remarks>
/// An aggregate describes the ENTITY and not the current view, which is why its slot is keyed without the page and
/// without the query and one read serves every page of that entity inside the window.
/// <para>
/// A source given NO memo reads no aggregate at all. That direction is deliberate: an uncached aggregate would be
/// paid on every page, and page-derived options under an honest label are the better of the two answers.
/// </para>
/// </remarks>
internal delegate Task<WhisparrResult<DiscoveryFacetOptions>> DiscoveryAggregateMemo(
    EntityKind kind,
    IReadOnlyList<string> remoteIds,
    Func<Task<WhisparrResult<DiscoveryFacetOptions>>> fetch,
    CancellationToken ct);
