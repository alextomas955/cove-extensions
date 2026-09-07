using System.Text.Json.Serialization;

namespace WhisparrSync.Discovery;

/// <summary>
/// The ThePornDB REST <c>GET /scenes?site_id=</c> response envelope — hand-rolled DTOs (records + source-gen
/// JSON, no codegen). ThePornDB's stash-box GraphQL <c>queryScenes</c> is gated — it returns a null
/// <c>scenes</c> array with a placeholder count equal to <c>per_page</c> for any filter (verified live) — so
/// an unmonitored v2 site's catalogue is read from the REST API instead, authenticated with the SAME per-account
/// token Cove stores for the theporndb.net metadata server (one token authenticates both surfaces).
/// </summary>
internal sealed record TpdbScenesResponse(
    [property: JsonPropertyName("data")] TpdbScene[]? Data,
    [property: JsonPropertyName("meta")] TpdbPageMeta? Meta);

/// <summary>The REST pagination block; <see cref="CurrentPage"/> reaching <see cref="LastPage"/> terminates the page loop.</summary>
internal sealed record TpdbPageMeta(
    [property: JsonPropertyName("current_page")] int CurrentPage,
    [property: JsonPropertyName("last_page")] int LastPage,
    [property: JsonPropertyName("total")] int? Total = null);

/// <summary>
/// One page of a ThePornDB <c>/scenes</c> read: the page's <see cref="Scenes"/>, <see cref="HasMore"/>, and the
/// source's advertised <see cref="Total"/>.
/// </summary>
/// <remarks>
/// <see cref="Total"/> is a real count up to 10,000 and SATURATES there (verified live: a narrow tag reports 7, a
/// site 392, a performer 1772, while any broader set reports exactly 10,000) — a lower bound at the ceiling.
/// End-of-catalogue is inferred from a SHORT page, never from the count.
/// </remarks>
internal sealed record TpdbScenePage(TpdbScene[] Scenes, bool HasMore, int? Total = null);

/// <summary>A whole-catalogue read, and whether the page ceiling cut it short.</summary>
internal sealed record TpdbCatalogue(TpdbScene[] Scenes, bool Truncated);

/// <summary>
/// One ThePornDB REST scene — the read-only projection the Missing-tab diff + card need.
/// </summary>
/// <remarks>
/// <see cref="Id"/> is the scene UUID; it is the SAME id namespace Cove stores as the video's ThePornDB remote
/// id (the REST id resolves via the stash-box <c>findScene</c> — verified live), so mapping it to
/// <c>WhisparrMovie.ForeignId</c> lets the pure diff subtract owned Cove scenes on the TPDB id family.
/// <see cref="Image"/> is the landscape cover, <see cref="Poster"/> the portrait; <see cref="Site"/> carries the
/// enclosing site's name. The enriched facets (<see cref="Image"/>, <see cref="Description"/>,
/// <see cref="Performers"/>, <see cref="Tags"/>) are appended last and default null; a fixture captured before
/// them still binds.
/// </remarks>
internal sealed record TpdbScene(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("poster")] string? Poster,
    [property: JsonPropertyName("site")] TpdbSceneSite? Site,
    [property: JsonPropertyName("image")] string? Image = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("performers")] TpdbPerformer[]? Performers = null,
    [property: JsonPropertyName("tags")] TpdbTag[]? Tags = null);

/// <summary>
/// The scene's enclosing site stub — <see cref="Name"/> stamps the missing row's entity (site) name, and
/// <see cref="Id"/> is the NUMERIC site id the <c>site_id=</c> filter keys on, present on every scene row.
/// </summary>
internal sealed record TpdbSceneSite(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("id")] int? Id = null);

/// <summary>
/// One entry of a scene's <c>performers</c> array — the chip name plus its avatar candidates. <see cref="Face"/>
/// is a 500×500 headshot (the best chip avatar), with <see cref="Image"/> then <see cref="Thumbnail"/> as
/// fallbacks. A non-owned scene has no Cove entity to link, so no Cove id is modeled; <see cref="Parent"/>
/// carries ThePornDB's own canonical identifiers.
/// </summary>
internal sealed record TpdbPerformer(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("face")] string? Face,
    [property: JsonPropertyName("image")] string? Image,
    [property: JsonPropertyName("thumbnail")] string? Thumbnail,
    [property: JsonPropertyName("parent")] TpdbPerformerParent? Parent = null);

/// <summary>
/// The canonical performer behind a scene APPEARANCE: <see cref="NumericId"/> is <c>parent._id</c> and
/// <see cref="Uuid"/> is <c>parent.id</c>.
/// </summary>
/// <remarks>
/// A scene row exposes FOUR performer identifiers and only ONE of them filters. <c>parent._id</c> is the
/// canonical NUMERIC id and <c>/scenes?performers[&lt;that&gt;]=1</c> narrows correctly. The other three each
/// returned <c>total: 0</c> when used as a filter — <c>performers[].id</c> is a per-scene APPEARANCE uuid,
/// <c>performers[]._id</c> an appearance numeric, and <c>parent.id</c> the canonical UUID (verified live, three
/// negative probes). A <c>total: 0</c> is indistinguishable from owning every scene, which is why the field a
/// filter is built from is named here, never left to a call site to pick.
/// <para>
/// <see cref="Uuid"/> is what a scene row is matched to the Cove entity by: Cove stores the canonical UUID, and
/// the numeric id is recoverable only from a row whose <c>parent.id</c> equals it.
/// </para>
/// </remarks>
internal sealed record TpdbPerformerParent(
    [property: JsonPropertyName("_id")] long? NumericId,
    [property: JsonPropertyName("id")] string? Uuid = null);

/// <summary>
/// One entry of a scene's <c>tags</c> array — the display <see cref="Name"/> the card chip renders, and
/// <see cref="Id"/>, the NUMERIC tag id the <c>tags[&lt;id&gt;]</c> object-map parameter keys on.
/// </summary>
internal sealed record TpdbTag(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("id")] int? Id = null);

/// <summary>
/// The <c>GET /tags?q=</c> lookup envelope — the one read that recovers a numeric ThePornDB tag id from a tag
/// NAME, for a Cove tag that stores no ThePornDB remote id.
/// </summary>
internal sealed record TpdbTagLookupResponse(
    [property: JsonPropertyName("data")] TpdbTagLookupRow[]? Data);

/// <summary>
/// One <c>/tags</c> row. <see cref="Id"/> is the NUMERIC id the scene filter keys on; the row's <c>uuid</c> is a
/// secondary field the filter does not accept.
/// </summary>
internal sealed record TpdbTagLookupRow(
    [property: JsonPropertyName("id")] int? Id,
    [property: JsonPropertyName("name")] string? Name);
