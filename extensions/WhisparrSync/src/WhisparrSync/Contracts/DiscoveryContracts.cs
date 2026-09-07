namespace WhisparrSync.Contracts;

/// <summary>
/// The <c>/discovery/entity</c> request body: the Cove entity id + a kind selector
/// (<c>"studio"</c>/<c>"performer"</c>), the optional page, and the optional <see cref="Query"/>. The ENTITY's
/// remote (StashDB/TPDB) id is resolved SERVER-SIDE from the Cove id; the body carries no url/key (the handler
/// uses the stored creds only). <c>Page</c> is the OPTIONAL 1-based catalogue page: omitted (null) means the
/// whole-catalogue re-derive (the shipped behavior), present drives the incremental paged direct read — one page
/// per request, REPLACING the rendered rows, never appending to them — clamped server-side (an over-range value
/// returns an empty page). Every field is nullable; a malformed body rejects cleanly with a 400.
/// </summary>
/// <remarks>
/// A caller DOES supply remote ids, on <see cref="Query"/>'s facet filters. What keeps that safe is structural:
/// the server-resolved entity criterion is present on every catalogue read and is ANDed with any client-supplied
/// filter id, which means a foreign id can only narrow THIS entity's own catalogue and can never widen the read
/// to another entity's (measured: a studio criterion plus a tag criterion returned 184 of that studio's 390).
/// Each id is shape-validated for the connected provider before it reaches a request, and a value failing that
/// check is dropped.
/// </remarks>
internal sealed record DiscoveryEntityRequest(
    int? CoveEntityId, string? Kind, int? Page = null, DiscoveryQueryRequest? Query = null);

/// <summary>
/// The provider-side ordering and facet narrowing a discovery request asks for — ONE record carried by the read
/// request and BOTH action requests, because a page index alone stops naming a re-derivable set once an ordering
/// exists. Every member is nullable; a malformed or older body still binds and rejects cleanly with a 400.
/// PascalCase, matching the shipped sibling request records.
/// </summary>
/// <remarks>
/// The ids are the metadata provider's own — a StashDB UUID on v3, a ThePornDB numeric id on v2 — never Cove ids.
/// <c>DiscoveryQueryGuard</c> shape-validates each one at the endpoint and DROPS a value it cannot trust, leaving
/// that axis unapplied while the read still succeeds.
/// <para>
/// Any dimension added here lands on all three requests at once, because this record IS all three requests'
/// query surface. <c>Page</c> stays a sibling field on the action requests where it already shipped: page and
/// query together form the coordinate, and re-shaping a field into this record buys nothing the shared record
/// does not already guarantee.
/// </para>
/// </remarks>
internal sealed record DiscoveryQueryRequest(
    string? Sort = null,
    string? StudioId = null,
    string? PerformerId = null,
    string? TagId = null,
    int? Year = null);

/// <summary>
/// The wire literals for the three orderings a discovery read can ask for — camelCase, byte-identical to the
/// client's own sort vocabulary. A <c>const string</c> class, never a serialized enum.
/// </summary>
internal static class DiscoverySortModes
{
    public const string Newest = "newest";
    public const string Oldest = "oldest";
    public const string Title = "title";
}

/// <summary>
/// The wire literals for the four facet axes a discovery read can narrow on — camelCase, byte-identical to the
/// client's own facet vocabulary. The year axis is <c>dateYear</c> because that is the client's key for it.
/// </summary>
internal static class DiscoveryFacetAxes
{
    public const string Studio = "studio";
    public const string Performer = "performer";
    public const string Tag = "tag";
    public const string Year = "dateYear";
}

/// <summary>
/// One selectable facet value: the provider's own <see cref="Id"/> (what a filter is sent as) and the
/// <see cref="Label"/> a control renders. BOTH providers filter by id and never by display name, which is why an
/// option is a pair.
/// </summary>
internal sealed record FacetOption(string Id, string Label);

/// <summary>
/// The whole-set option values a source read from the provider's own aggregate, per axis. An axis is null when
/// the provider offers no aggregate for it; the caller then derives that axis's options from the rendered rows.
/// </summary>
internal sealed record DiscoveryFacetOptions(
    FacetOption[]? Studios = null,
    FacetOption[]? Performers = null,
    FacetOption[]? Tags = null,
    FacetOption[]? Years = null);

/// <summary>
/// The <c>/discovery/action</c> request body: the entity context (<see cref="CoveEntityId"/> + <see cref="Kind"/>),
/// the client-held stable <see cref="SourceId"/> (received from this extension's own <c>/discovery/entity</c>
/// projection — validated server-side against the re-derived missing set before any action, so it can only ever
/// name a non-owned catalogue scene), and the <see cref="Op"/> selector (<c>"monitor"</c> / <c>"unmonitor"</c> /
/// <c>"search"</c> — only <c>"search"</c> issues an immediate grab).
/// The body carries no url/key — the handler uses the stored creds only. Every field nullable so a malformed
/// body rejects cleanly with a 400. PascalCase to match the shipped sibling request record
/// (<see cref="DiscoveryEntityRequest"/>).
/// <c>Page</c> is the OPTIONAL 1-based coordinate of the rendered page <see cref="SourceId"/> came from:
/// omitted (null) re-derives the whole catalogue (the shipped behavior), present bounds the re-derive to that one
/// page and is clamped server-side. A coordinate that does not contain the id fails CLOSED — the existing
/// not-in-missing-set refusal, with no mutation.
/// </summary>
internal sealed record DiscoveryActionRequest(
    int? CoveEntityId, string? Kind, string? SourceId, string? Op, int? Page = null,
    DiscoveryQueryRequest? Query = null);

/// <summary>
/// The <c>/discovery/action-all</c> request body: the entity context (<see cref="CoveEntityId"/> + <see cref="Kind"/>),
/// the <see cref="Op"/> selector (<c>"monitor"</c> / <c>"unmonitor"</c> / <c>"search"</c>), and an OPTIONAL <see cref="SourceIds"/>
/// selection. <see cref="SourceIds"/> omitted (null) means the whole re-derived missing set (a whole-entity
/// "mark all"); present means a client-held selection subset, intersected server-side with the re-derived missing
/// set before any add (so an out-of-set id is skipped — an action can only ever target a non-owned catalogue
/// scene). The body carries no url/key — the handler uses the stored creds only. Every field nullable so a
/// malformed body rejects cleanly with a 400. PascalCase to match the shipped sibling request records.
/// <c>Page</c> is the OPTIONAL 1-based coordinate of the rendered page a supplied <see cref="SourceIds"/>
/// selection came from: omitted (null) re-derives the whole catalogue, which is the point of the whole-entity
/// "mark all" and stays its behavior. A coordinate that does not contain an id skips that id, with no mutation.
/// </summary>
internal sealed record DiscoveryActionAllRequest(
    int? CoveEntityId, string? Kind, string? Op, string[]? SourceIds, int? Page = null,
    DiscoveryQueryRequest? Query = null);

/// <summary>
/// One missing scene in the per-entity discovery projection — a scene the entity's direct metadata source
/// (StashDB on v3, ThePornDB on v2) offers that Cove does not own. An all-camelCase projection DTO (never a live
/// domain/EF type).
/// </summary>
/// <param name="SourceId">The scene's stable StashDB/TPDB id — the diff key.</param>
/// <param name="Title">The scene title (Whisparr-sourced); null when the row carries none.</param>
/// <param name="ReleaseDate">The scene's release date string; null/omitted when absent.</param>
/// <param name="EntityName">The offering studio/performer display name (carried per-row for the meta line + empty-state copy).</param>
/// <param name="PosterUrl">The poster image url when the row carries a poster; null renders a fallback tile.</param>
/// <param name="CoverUrl">
/// The landscape (16:9) cover the card renders through an <c>&lt;img&gt;</c>; null falls the card back to
/// <see cref="PosterUrl"/>, then to a fallback tile. Preferred over the portrait poster for the wide card media box.
/// </param>
/// <param name="StudioName">The scene's own studio title — the card's meta line (per-row, so a performer's rows can name different studios); null when absent.</param>
/// <param name="Performers">
/// The scene's performers (name + avatar url) the card renders as chips. Populated whenever the direct source's
/// row names any performer; null (no chip strip) only when the row names none. The avatar url is the direct
/// source's own image, else null so the chip renders its placeholder glyph.
/// </param>
/// <param name="Tags">The scene's tag labels — surfaced as the card's footer tag COUNT; same null-when-absent availability as <paramref name="Performers"/>.</param>
/// <param name="Overview">The scene blurb the card's 2-line description renders — the direct source's own details. Null/omitted when the source carries none.</param>
/// <param name="Status">
/// The scene's Whisparr status — one of the <see cref="DiscoveryMissingStatus"/> values. The
/// <see cref="DiscoveryMissingStatus.NotAdded"/> default keeps an older/synthetic payload mapping cleanly; a
/// server that cannot assert a status sends <see cref="DiscoveryMissingStatus.Unknown"/> explicitly, never an
/// omitted field.
/// </param>
internal sealed record MissingScene(
    string SourceId,
    string? Title,
    string? ReleaseDate,
    string? EntityName,
    string? PosterUrl,
    string? CoverUrl = null,
    string? StudioName = null,
    MissingPerformer[]? Performers = null,
    string[]? Tags = null,
    string? Overview = null,
    string Status = DiscoveryMissingStatus.NotAdded);

/// <summary>
/// One performer on a <see cref="MissingScene"/> — the display <see cref="Name"/> and an optional avatar
/// <see cref="ImageUrl"/> (empty/absent when the source carries no image, so the chip falls back to a
/// placeholder). camelCase wire; a non-owned scene links to no Cove entity, so no id is carried.
/// </summary>
internal sealed record MissingPerformer(string Name, string? ImageUrl);

/// <summary>
/// The Whisparr statuses a MISSING scene can carry — a distinct, narrower vocabulary than the four-state scene
/// status enum. A missing scene is by definition neither downloaded nor excluded (both are subtracted from the
/// catalogue before the missing set is formed), which leaves three assertable states: the row is absent from
/// Whisparr (<see cref="NotAdded"/>), present-and-monitored with no file (<see cref="Wanted"/>), or
/// present-but-unmonitored (<see cref="Unmonitored"/>). <see cref="Unknown"/> is the fourth value and asserts
/// none of them, because the read those three rest on is not always available. camelCase to match the rest of
/// the wire.
/// </summary>
internal static class DiscoveryMissingStatus
{
    /// <summary>Whisparr has no row for the scene — the direct-provider catalogue's default (a synthesized row is not a real added movie).</summary>
    public const string NotAdded = "notAdded";

    /// <summary>A Whisparr movie row exists and is monitored but has no file — it is on the wanted list.</summary>
    public const string Wanted = "wanted";

    /// <summary>A Whisparr movie row exists but is not monitored — present yet deliberately untracked.</summary>
    public const string Unmonitored = "unmonitored";

    /// <summary>
    /// No Whisparr status is assertable for the scene: the connected generation cannot correlate it to a Whisparr
    /// row (v2 builds no StashDB-keyed reconciliation index), or the movie-set read did not answer. Abstaining
    /// keeps an outage from reading as a per-scene verdict — the same rule
    /// <see cref="SceneStatus.SceneDetail.CutoffMet"/> follows for a cutoff Whisparr does not report.
    /// </summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// The per-entity discovery outcome the client renders. The four <see cref="DiscoveryResult.State"/> values are
/// decided SERVER-SIDE (never client-asserted) from the resolved remote id + whether Cove has a matching
/// metadata credential + the direct catalogue read outcome. All four are 200 responses — a
/// configuration/availability state is not an HTTP error.
/// </summary>
internal static class DiscoveryState
{
    /// <summary>A served catalogue (possibly empty — the honest own-everything). The default.</summary>
    public const string Ok = "ok";

    /// <summary>A remote id resolved but Cove has no matching metadata credential for the connected version's source — an actionable "set up a metadata source in Cove" state, never a misleading empty list.</summary>
    public const string NeedsProviderKey = "needsProviderKey";

    /// <summary>No remote (StashDB/TPDB) id resolved for the entity — nothing to enumerate (distinct from own-everything).</summary>
    public const string NoSourceId = "noSourceId";

    /// <summary>The direct metadata source (StashDB/TPDB) could not be reached — a state distinct from own-everything.</summary>
    public const string SourceUnreachable = "sourceUnreachable";
}

/// <summary>The metadata source a <see cref="DiscoveryResult"/> was served from — names the source in the UI copy so a view is honest about which system it read. A served result is always a direct source (StashDB/TPDB); the Whisparr label is an inert defensive value for a terminal/older payload's default.</summary>
internal static class DiscoverySourceLabel
{
    public const string Whisparr = "whisparr";
    public const string StashDb = "stashdb";
    public const string Tpdb = "tpdb";
}

/// <summary>
/// The <c>/discovery/entity</c> response: the missing-scene list, the entity display name (surfaced once for the
/// empty-state copy), the discriminated <see cref="State"/>, and the <see cref="Source"/> label. camelCase wire.
/// A served result is always a direct source; <see cref="State"/>/<see cref="Source"/> default to
/// <c>ok</c>/<c>whisparr</c> as an inert defensive value so a terminal/older payload maps cleanly. <see cref="Version"/> is the
/// connected Whisparr generation (<c>"v3"</c>/<c>"v2"</c>) so the client can gate a v3-only action (per-scene
/// Monitor) without a second options round-trip; it defaults so an older/synthetic payload maps cleanly.
/// The paging trio is the incremental read's cursor: <c>NextPage</c> is the next page index to request (null
/// when no further page remains), <c>HasMore</c> whether a further page remains, and <c>Total</c> the full
/// catalogue size when the source advertises one. All three default to the non-paged values (null/false/null),
/// so the whole-catalogue re-derive and an older payload never advertise more. <c>IsParent</c> is true when the
/// studio unions child sub-studios into this catalogue — the signal the child-studio facet gates on; it defaults
/// false so a non-parent studio, every other kind, and an older payload map cleanly.
/// <c>ServerSideSorts</c> names the orderings the connected provider applied over the WHOLE catalogue, in the
/// <see cref="DiscoverySortModes"/> vocabulary. Null or absent means none: the client then words its sort control
/// as an ordering of the rows it loaded, which is the truth for a provider with no sort axis.
/// <c>ServerSideFacets</c> is the same statement for the four narrowing axes, in the
/// <see cref="DiscoveryFacetAxes"/> vocabulary — a CAPABILITY, true of any selection the reader might make.
/// <c>WholeSetFacetAxes</c> is a narrower and per-READ claim: the axes whose <c>FacetOptions</c> list came from
/// an aggregate over the whole catalogue on THIS read. Every axis absent from it offers the values the returned
/// rows carry, and its control says so. Null on every non-served state and on the whole-catalogue re-derive,
/// which the client reads as supports-nothing and page-derived — the safe default in both directions.
/// </summary>
/// <remarks>
/// <c>Total</c> is the FILTERED catalogue total: the count of the set the query narrowed to, before Cove
/// subtracts what it already owns. It is therefore an OVER-estimate of the missing count, exactly as it has
/// always been, and the label it carries is unchanged. An exact missing count is not computable without an
/// unbounded crawl of the whole catalogue, and is deliberately not attempted.
/// </remarks>
internal sealed record DiscoveryResult(
    IReadOnlyList<MissingScene> Scenes,
    string? EntityName,
    string State = DiscoveryState.Ok,
    string Source = DiscoverySourceLabel.Whisparr,
    string Version = "v3",
    int? NextPage = null,
    bool HasMore = false,
    int? Total = null,
    bool IsParent = false,
    bool TotalIsAtLeast = false,
    string[]? ServerSideSorts = null,
    string[]? ServerSideFacets = null,
    string[]? WholeSetFacetAxes = null,
    DiscoveryFacetOptions? FacetOptions = null,
    bool CatalogueTruncated = false);

/// <summary>
/// The <c>/discovery/count</c> answer: how many scenes the entity's metadata source lists that Cove does not
/// own. Falls back to the computed page count when the source reports no total.
/// </summary>
internal sealed record DiscoveryCountResponse(int Count);

/// <summary>
/// The discovery unmonitor answer. <see cref="Unmonitored"/> is false for a scene Whisparr does not hold —
/// nothing to unmonitor, and never an add.
/// </summary>
internal sealed record SceneUnmonitorResponse(bool Unmonitored);
