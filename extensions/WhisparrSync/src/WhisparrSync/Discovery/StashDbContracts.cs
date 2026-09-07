using System.Text.Json.Serialization;

namespace WhisparrSync.Discovery;

/// <summary>
/// The StashDB GraphQL <c>queryScenes</c> request envelope — a hand-rolled DTO (records + source-gen JSON, no
/// codegen) serialized as <c>{ query, variables }</c>. The query string is a fixed read-only
/// <c>queryScenes</c> selection owned by <see cref="StashDbGraphQlClient"/>; only the studio id + page vary per
/// call, carried in <see cref="Variables"/>.
/// </summary>
internal sealed record StashDbGraphQlRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] StashDbQueryVariables Variables);

/// <summary>The single GraphQL variable — the whole <c>SceneQueryInput</c> passed as <c>$input</c>.</summary>
internal sealed record StashDbQueryVariables(
    [property: JsonPropertyName("input")] StashDbSceneQueryInput Input);

/// <summary>
/// The StashDB <c>SceneQueryInput</c> for a catalogue read. GraphQL has no JSON enum, so the enum-typed fields
/// (each criterion's modifier, <see cref="Sort"/>, <see cref="Direction"/>) travel as strings. An unset criterion
/// is null and dropped from the wire by the source-gen context, keeping an empty criterion StashDB would reject
/// off the request.
/// </summary>
/// <remarks>
/// The criteria AND together in ONE request (verified live on a 390-scene studio: studios plus performers
/// returned 57, studios plus tags 184, studios plus tags plus performers 25; performers plus tags returned 410).
/// A catalogue read always carries the ENTITY's own criterion, and a caller-supplied facet filter is an
/// ADDITIONAL criterion narrowing within it — the result is necessarily a subset of that entity's catalogue and
/// can never widen to a foreign one.
/// </remarks>
internal sealed record StashDbSceneQueryInput(
    [property: JsonPropertyName("page")] int Page,
    // StashDB's wire name is snake_case; the request naming policy is camelCase, so this one is pinned explicitly.
    [property: JsonPropertyName("per_page")] int PerPage,
    [property: JsonPropertyName("sort")] string Sort,
    [property: JsonPropertyName("direction")] string Direction,
    [property: JsonPropertyName("studios")] StashDbIdCriterion? Studios = null,
    [property: JsonPropertyName("performers")] StashDbIdCriterion? Performers = null,
    [property: JsonPropertyName("tags")] StashDbIdCriterion? Tags = null,
    [property: JsonPropertyName("date")] StashDbDateCriterion? Date = null);

/// <summary>A StashDB <c>MultiIDCriterionInput</c>: the id set plus the match modifier (e.g. <c>INCLUDES</c>).</summary>
internal sealed record StashDbIdCriterion(
    [property: JsonPropertyName("value")] string[] Value,
    [property: JsonPropertyName("modifier")] string Modifier);

/// <summary>
/// A StashDB <c>DateCriterionInput</c>: one date <see cref="Value"/> and one <see cref="Modifier"/>.
/// </summary>
/// <remarks>
/// The input accepts exactly one of each (introspected), which is why a closed release-year window is not
/// expressible as a criterion and the year axis is emulated by <see cref="StashDbGraphQlClient"/> from a single
/// half-open bound.
/// </remarks>
internal sealed record StashDbDateCriterion(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("modifier")] string Modifier);

/// <summary>
/// The StashDB <c>queryScenes</c> response envelope (<c>{ data: { queryScenes: { count, scenes[] } } }</c>).
/// A GraphQL error-only response carries a null <see cref="Data"/> — a classified state, not a throw. Every
/// field is nullable so a partial/odd body degrades rather than crashing the parse.
/// </summary>
internal sealed record StashDbGraphQlResponse(StashDbQueryScenesData? Data);

/// <summary>The <c>data</c> object carrying the single queried field.</summary>
internal sealed record StashDbQueryScenesData(StashDbQueryScenesResult? QueryScenes);

/// <summary>
/// The <c>queryScenes</c> result: <see cref="Count"/> is the studio's full catalogue size (the pagination
/// terminator), <see cref="Scenes"/> the current page's rows.
/// </summary>
internal sealed record StashDbQueryScenesResult(int Count, StashDbScene[]? Scenes);

/// <summary>
/// One page of a <c>queryScenes</c> read: the page's <see cref="Scenes"/>, the advertised full-catalogue
/// <see cref="Count"/> (the total the tab shows and paging terminates on), and <see cref="HasMore"/> — whether a
/// further page remains. Returned by the single-page read so the caller drives pagination.
/// </summary>
internal sealed record StashDbScenePage(StashDbScene[] Scenes, int Count, bool HasMore);

/// <summary>A whole-catalogue read, and whether the page ceiling cut it short.</summary>
/// <remarks>
/// <c>Truncated</c> is the discriminator that keeps a capped read distinguishable from an exhausted one. The
/// rows alone cannot carry it: a caller receiving only the array must treat a partial catalogue as complete,
/// which under-reports missing scenes for exactly the largest entities.
/// </remarks>
internal sealed record StashDbCatalogue(StashDbScene[] Scenes, bool Truncated);

/// <summary>
/// One StashDB scene row — the read-only projection the Missing-tab diff + card need. <see cref="Id"/> is the
/// StashDB UUID the diff keys on (mapped to a <c>WhisparrMovie.StashId</c>); <see cref="ReleaseDate"/> is
/// StashDB's snake_case <c>release_date</c>. A StashDB scene typically carries only screenshots, no poster.
/// <see cref="Performers"/>/<see cref="Tags"/> are the content facets the card chips render — the direct
/// provider is the ONLY path that carries them (a through-Whisparr movie row exposes neither). Both default null
/// so a captured fixture predating them still binds.
/// </summary>
internal sealed record StashDbScene(
    string Id,
    string? Title,
    [property: JsonPropertyName("release_date")] string? ReleaseDate,
    StashDbStudio? Studio,
    StashDbImage[]? Images,
    StashDbScenePerformer[]? Performers = null,
    StashDbSceneTag[]? Tags = null,
    string? Details = null);

/// <summary>A StashDB scene's studio stub — the <see cref="Name"/> stamps the missing row's entity name.</summary>
internal sealed record StashDbStudio(string? Id, string? Name);

/// <summary>A StashDB scene image entry (screenshot). Only <see cref="Url"/> is modeled.</summary>
internal sealed record StashDbImage(string? Url);

/// <summary>
/// One entry of a StashDB scene's <c>performers</c> array — a join node wrapping the performer stub. The name
/// lives one level in, on <see cref="Performer"/>, matching the StashDB <c>performers { performer { name } }</c>
/// selection shape.
/// </summary>
internal sealed record StashDbScenePerformer(StashDbPerformer? Performer);

/// <summary>
/// The performer stub inside a <see cref="StashDbScenePerformer"/> — the display <see cref="Name"/>, the
/// performer's <see cref="Images"/> (the first is the chip avatar), and <see cref="Id"/>.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is the PROVIDER's own filter id — the value a <c>performers INCLUDES</c> criterion is sent
/// as. It is not a Cove entity id and never links a chip to one: a non-owned scene has no Cove entity behind it.
/// Appended last with a null default, keeping a fixture captured before it bindable.
/// </remarks>
internal sealed record StashDbPerformer(
    string? Name,
    StashDbImage[]? Images = null,
    [property: JsonPropertyName("id")] string? Id = null);

/// <summary>
/// One entry of a StashDB scene's <c>tags</c> array — the display <see cref="Name"/> the card chip renders, and
/// <see cref="Id"/>, the provider's own filter id a <c>tags INCLUDES</c> criterion is sent as. Appended last with
/// a null default, keeping a fixture captured before it bindable.
/// </summary>
internal sealed record StashDbSceneTag(
    string? Name,
    [property: JsonPropertyName("id")] string? Id = null);

/// <summary>The <c>findTagOrAlias</c> request variables — a tag NAME (or one of its aliases).</summary>
internal sealed record StashDbTagNameVariables(
    [property: JsonPropertyName("name")] string Name);

/// <summary>
/// The <c>findTagOrAlias</c> response: the single tag matching the name or one of its aliases, else null. Unlike a
/// fuzzy search this resolver is exact by construction, so there is no ambiguity to arbitrate.
/// </summary>
internal sealed record StashDbTagLookupResponse(
    [property: JsonPropertyName("data")] StashDbTagLookupData? Data);

internal sealed record StashDbTagLookupData(
    [property: JsonPropertyName("findTagOrAlias")] StashDbTagLookupRow? FindTagOrAlias);

internal sealed record StashDbTagLookupRow(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name);

/// <summary>A <c>findTagOrAlias</c> request envelope (its variables differ from the scene query's).</summary>
internal sealed record StashDbTagNameRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] StashDbTagNameVariables Variables);

/// <summary>
/// The StashDB <c>PerformerQueryInput</c> for the studio-roster aggregate: every performer attributed to one
/// studio's scenes, asked for in a single page under an explicit ordering.
/// </summary>
/// <remarks>
/// <see cref="Sort"/> and <see cref="Direction"/> are not cosmetic. Walking this aggregate page by page under the
/// provider's default ordering returns exactly <c>count</c> rows of which some are repeats of an earlier page: one
/// studio advertising 565 yielded 550 distinct performers that way, while <c>NAME</c>/<c>ASC</c> yielded all 565
/// (both measured live). A stable ordering plus a single page removes that instability at its source.
/// </remarks>
internal sealed record StashDbPerformerQueryInput(
    [property: JsonPropertyName("studio_id")] string StudioId,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("per_page")] int PerPage,
    [property: JsonPropertyName("sort")] string Sort,
    [property: JsonPropertyName("direction")] string Direction);

/// <summary>The single GraphQL variable for the roster aggregate — the whole <c>PerformerQueryInput</c>.</summary>
internal sealed record StashDbPerformerQueryVariables(
    [property: JsonPropertyName("input")] StashDbPerformerQueryInput Input);

/// <summary>A <c>queryPerformers</c> request envelope (its variables differ from the scene query's).</summary>
internal sealed record StashDbPerformerQueryRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] StashDbPerformerQueryVariables Variables);

/// <summary>The <c>queryPerformers</c> response envelope. A GraphQL error-only response carries a null Data.</summary>
internal sealed record StashDbPerformerQueryResponse(
    [property: JsonPropertyName("data")] StashDbPerformerQueryData? Data);

internal sealed record StashDbPerformerQueryData(
    [property: JsonPropertyName("queryPerformers")] StashDbPerformerQueryResult? QueryPerformers);

/// <summary>
/// The roster aggregate's result: <see cref="Count"/> is how many performers the studio has in total and
/// <see cref="Performers"/> the rows this one page carried.
/// </summary>
/// <remarks>
/// The two disagree when the roster is larger than the requested page, which is the only signal that the list is
/// a prefix and not the whole roster — <c>count</c> is what makes the truncation detectable at all.
/// </remarks>
internal sealed record StashDbPerformerQueryResult(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("performers")] StashDbAggregateRow[]? Performers);

/// <summary>
/// One <c>{id, name}</c> row of a whole-set aggregate — a studio's performer, or a performer's studio. Both
/// fields are nullable: an odd row is then dropped by the option builder, and the parse of the rest survives.
/// </summary>
internal sealed record StashDbAggregateRow(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("name")] string? Name);

/// <summary>A studio's whole performer roster: the rows returned, and the <see cref="Count"/> the provider holds.</summary>
internal sealed record StashDbPerformerRoster(StashDbAggregateRow[] Rows, int Count);

/// <summary>The <c>findPerformer</c> variables — the performer's StashDB id.</summary>
internal sealed record StashDbPerformerIdVariables(
    [property: JsonPropertyName("id")] string Id);

/// <summary>A <c>findPerformer</c> request envelope.</summary>
internal sealed record StashDbPerformerStudiosRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] StashDbPerformerIdVariables Variables);

/// <summary>The <c>findPerformer</c> response envelope for the studio-list aggregate.</summary>
internal sealed record StashDbPerformerStudiosResponse(
    [property: JsonPropertyName("data")] StashDbPerformerStudiosData? Data);

internal sealed record StashDbPerformerStudiosData(
    [property: JsonPropertyName("findPerformer")] StashDbPerformerStudios? FindPerformer);

/// <summary>
/// A performer's studio list. The provider returns it as ONE unpaged field, not as a paged connection.
/// </summary>
/// <remarks>
/// Being unpaged is why this aggregate needs no ordering and no completeness check: there is no page boundary for
/// a row to slip across. One performer returned 249 studios in a single field, with none of the studios seen on
/// sampled pages of that performer's own catalogue absent from it.
/// </remarks>
internal sealed record StashDbPerformerStudios(
    [property: JsonPropertyName("studios")] StashDbPerformerStudioEntry[]? Studios);

/// <summary>One entry of a performer's studio list — a join node wrapping the studio stub.</summary>
internal sealed record StashDbPerformerStudioEntry(
    [property: JsonPropertyName("studio")] StashDbAggregateRow? Studio);
