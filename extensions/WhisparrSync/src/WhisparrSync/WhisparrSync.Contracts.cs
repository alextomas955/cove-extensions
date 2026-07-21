using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using WhisparrSync.Matching;
using WhisparrSync.Options;
using WhisparrSync.Scene;

namespace WhisparrSync;

/// <summary>
/// The wire contracts: the response <c>JsonSerializerOptions</c> statics and every request/response DTO
/// the endpoint handlers bind and return. Grouping them keeps the camelCase wire shape in one place.
/// </summary>
public sealed partial class WhisparrSync
{
    // Serialize responses with the extension's own web-convention options so the wire shape (camelCase)
    // matches what the UI reads; the host's default minimal-API serializer is not relied on here.
    private static readonly JsonSerializerOptions TestConnectionResponseJsonOptions = new(JsonSerializerDefaults.Web);

    // The options / list responses keep property names AS DECLARED (PascalCase — the stored-blob spelling
    // the UI models), with the one deliberate exception the [JsonPropertyName] on OptionsView.HasApiKey
    // renders as `hasApiKey`. A default (non-Web) options instance applies no naming policy.
    private static readonly JsonSerializerOptions OptionsResponseJsonOptions = new();

    // The reconciliation responses are a fresh UI contract (no stored-blob spelling to preserve), so they use
    // the camelCase Web convention, and the JsonStringEnumConverter renders MatchedBy / MatchStatus as their
    // string names ("StashId" / "Tpdb" / "Confirmed" …) rather than integers the UI would have to decode.
    private static readonly JsonSerializerOptions ReconciliationResponseJsonOptions = CoveJsonOptions.WebWithEnumStrings();

    // The import-log is a fresh UI contract (no stored-blob spelling to preserve), so it uses the camelCase
    // Web convention; the JsonStringEnumConverter renders any enum-typed field as its string name for the UI.
    private static readonly JsonSerializerOptions ImportLogResponseJsonOptions = CoveJsonOptions.WebWithEnumStrings();

    // The monitor responses are a fresh UI contract (the EntityMonitorResult / EntityStatus records), so they
    // use the camelCase Web convention; the JsonStringEnumConverter renders any enum-typed field as its string
    // name. Mirrors ReconciliationResponseJsonOptions.
    private static readonly JsonSerializerOptions MonitorResponseJsonOptions = CoveJsonOptions.WebWithEnumStrings();

    // The batch map's value is a SceneCardStatus { state, hasFile }; its State carries a property-level
    // [JsonConverter], but registering SceneWhisparrStateJsonConverter on the options too pins the camelCase wire
    // strings even for any bare-enum value (a plain options-level JsonStringEnumConverter would otherwise win
    // over the enum's type-level attribute and emit PascalCase — the pitfall the enum's own docs warn about).
    // The frontend status store keys on those exact strings; hasFile is a plain bool secondary signal.
    private static readonly JsonSerializerOptions SceneStatusBatchJsonOptions = BuildSceneStatusBatchOptions();

    private static JsonSerializerOptions BuildSceneStatusBatchOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new SceneWhisparrStateJsonConverter());
        return options;
    }

    /// <summary>The Test-connection request body: the URL + key the user typed on the settings page.</summary>
    internal sealed record TestConnectionRequest(string? BaseUrl, string? ApiKey);

    /// <summary>
    /// The <c>/register-webhook</c> body: the (possibly hand-edited) webhook <see cref="Url"/> the UI shows.
    /// Only its origin (scheme+host) is honored server-side; the token is always re-minted from the stored secret.
    /// Null (an empty POST) falls back to the request host, preserving the pre-edit behavior.
    /// </summary>
    internal sealed record WebhookRegisterRequest(string? Url);

    /// <summary>
    /// The options-save request body. Case-insensitive minimal-API binding maps the UI's PascalCase JSON
    /// onto these. An empty/absent <see cref="ApiKey"/> preserves the stored key (write-only). The
    /// add-defaults (<see cref="TagsOnAdd"/>, <see cref="MonitorNewByDefault"/>,
    /// <see cref="AllowQualityUpgrades"/>) are nullable so an absent field PRESERVES the stored value
    /// (a partial save never resets an unrelated toggle — <see cref="WhisparrOptions.WithSubmitted"/>).
    /// The design's "Search on add" is deliberately NOT a field here: loop-safety is LOCKED, so an add never
    /// auto-searches and there is nothing for the UI to bind to a wire toggle.
    /// </summary>
    internal sealed record OptionsSaveRequest(
        string? BaseUrl, string? ApiKey, string? SelectedVersion, int QualityProfileId,
        IReadOnlyList<PathTranslationRule>? PathTranslation = null,
        IReadOnlyList<string>? TagsOnAdd = null, bool? MonitorNewByDefault = null, bool? AllowQualityUpgrades = null);

    /// <summary>
    /// The confirm/reject request body: the Cove video id + Whisparr movie id of the needs-review suggestion the
    /// user is acting on. Validated against the freshly-computed diff before any write (a forged pair is refused).
    /// </summary>
    internal sealed record MatchDecisionRequest(int CoveId, int WhisparrMovieId);

    /// <summary>
    /// The <c>/monitor</c> request body: the entity <see cref="Kind"/> (<c>"studio"</c> /
    /// <c>"performer"</c>), the entity's own Cove <see cref="RemoteIds"/> forwarded straight from the slot
    /// context, and the target <see cref="Monitored"/> state. It carries NO base URL or API key — the handler
    /// uses the stored creds only, and resolves the Whisparr lookup id server-side from
    /// <see cref="RemoteIds"/> (the pair whose endpoint matches the stored StashDbEndpoint). Fields are
    /// nullable so a malformed body is rejected cleanly rather than throwing on bind.
    /// </summary>
    internal sealed record MonitorRequest(string? Kind, RemoteIdInput[]? RemoteIds, bool Monitored, string? Scope = null);

    /// <summary>
    /// The <c>/monitor-status</c> request body: the entity <see cref="Kind"/> + its Cove
    /// <see cref="RemoteIds"/>. Like <see cref="MonitorRequest"/> it carries no url/key.
    /// </summary>
    internal sealed record MonitorStatusRequest(string? Kind, RemoteIdInput[]? RemoteIds);

    /// <summary>
    /// The <c>/scene-detail</c> request body: ONLY the scene's Cove entity id. The scene's StashDB
    /// identity is resolved SERVER-SIDE from this id (never a caller-supplied remote id), and the body carries
    /// no url/key — the handler uses the stored creds only.
    /// </summary>
    internal sealed record SceneDetailRequest(int CoveId);

    /// <summary>
    /// The <c>/scene-releases-list</c> request body (on-expand): ONLY the scene's Cove entity id. Same
    /// server-side identity resolution + stored-creds-only posture as <see cref="SceneDetailRequest"/>.
    /// </summary>
    internal sealed record SceneReleasesRequest(int CoveId);

    /// <summary>
    /// The <c>/scene-add</c> request body: ONLY the scene's Cove entity id. The scene's
    /// StashDB identity + title are resolved SERVER-SIDE from this id (never caller-supplied), and the body
    /// carries no url/key — the handler uses the stored creds only. Mirrors <see cref="SceneDetailRequest"/>.
    /// </summary>
    internal sealed record SceneAddRequest(int CoveId);

    /// <summary>
    /// The <c>/scene-search</c> request body: ONLY the scene's Cove entity id. Same server-side
    /// identity resolution + stored-creds-only posture as <see cref="SceneAddRequest"/>.
    /// </summary>
    internal sealed record SceneSearchRequest(int CoveId);

    /// <summary>
    /// The <c>/scene-monitor</c> request body: the scene's Cove entity id + the target
    /// <see cref="Monitored"/> state. The scene's StashDB identity is resolved server-side from
    /// <see cref="CoveId"/>; the body carries no url/key.
    /// </summary>
    internal sealed record SceneMonitorRequest(int CoveId, bool Monitored);

    /// <summary>
    /// The <c>/bulk-add-missing</c> request body: the entity <see cref="Kind"/> (<c>"studio"</c> /
    /// <c>"performer"</c>) + the Cove <see cref="CoveEntityId"/> (<c>Studio.Id</c> / <c>Performer.Id</c>) whose
    /// OWN scenes are enumerated for the local diff. It carries NO url/key and — deliberately — NO
    /// <c>remoteIds</c>: the missing-set diff is keyed by the Cove entity id, never by a forwarded
    /// stashId, so a <c>remoteIds</c> field would be a dead input. <see cref="Kind"/> is nullable so a malformed
    /// body is rejected cleanly (400) rather than throwing on bind.
    /// </summary>
    internal sealed record BulkAddMissingRequest(string? Kind, int CoveEntityId);

    /// <summary>
    /// The <c>/bulk-search-monitored</c> request body: the entity <see cref="Kind"/> + its Cove
    /// <see cref="RemoteIds"/>. The entity's StashDB id is resolved server-side from <see cref="RemoteIds"/> (the
    /// pair whose endpoint matches the stored StashDbEndpoint), exactly like <see cref="MonitorRequest"/>; the
    /// body carries no url/key. Fields are nullable so a malformed body is rejected cleanly.
    /// </summary>
    internal sealed record BulkSearchMonitoredRequest(string? Kind, RemoteIdInput[]? RemoteIds);

    /// <summary>
    /// The <c>/reflect-owned</c> request body: the entity <see cref="Kind"/> (<c>"studio"</c> /
    /// <c>"performer"</c>) + the Cove <see cref="CoveEntityId"/> (<c>Studio.Id</c> / <c>Performer.Id</c>) whose
    /// OWN scenes are enumerated for the owned-scene import. Like <see cref="BulkAddMissingRequest"/> it carries
    /// NO url/key and NO <c>remoteIds</c>: the match is keyed by the Cove entity id + each video's own TPDB id,
    /// never a forwarded stashId. <see cref="Kind"/> is nullable so a malformed body is rejected cleanly (400).
    /// </summary>
    internal sealed record ReflectOwnedRequest(string? Kind, int CoveEntityId);

    /// <summary>
    /// The <c>/scene-exclusion</c> request body: the scene's Cove id + the target
    /// <see cref="Exclude"/> state (true adds the exclusion, false removes it). The scene's StashDB identity is
    /// resolved SERVER-SIDE from <see cref="CoveId"/> (never a caller-supplied StashDB/exclusion id — the
    /// un-exclude id is matched by the adapter via foreignId), and the body carries no url/key — the handler
    /// uses the stored creds only.
    /// </summary>
    internal sealed record SceneExclusionRequest(int CoveId, bool Exclude);

    /// <summary>
    /// The <c>/scene-grab-release</c> request body: the scene's Cove id + the picked release's
    /// <see cref="Guid"/> and <see cref="IndexerId"/>. The scene identity is resolved server-side from
    /// <see cref="CoveId"/>; the guid/indexerId are release handles the picker obtained from this extension's
    /// own <c>/scene-releases-list</c> read. The body carries no url/key; the guid is never echoed to a
    /// log. Fields are nullable so a malformed body is rejected cleanly.
    /// </summary>
    internal sealed record SceneGrabReleaseRequest(int CoveId, string? Guid, int IndexerId);

    /// <summary>
    /// The <c>/videos-batch</c> request body: the <see cref="Op"/>
    /// (<c>add</c>/<c>search</c>/<c>searchUpgrades</c>/<c>exclude</c>/<c>unExclude</c>, case-insensitive) and the
    /// selected Cove video ids. Every id is resolved to a scene SERVER-SIDE (never a caller-supplied StashDB id),
    /// the list is capped before any per-item work (<see cref="MaxEntityIdsPerRequest"/>), and the body
    /// carries no url/key — the handler uses the stored creds only. Per-op grab posture: only
    /// <c>search</c>/<c>searchUpgrades</c> may grab; <c>add</c>/<c>exclude</c>/<c>unExclude</c> never search
    /// (loop-safety is LOCKED). Fields are nullable so a malformed body is rejected cleanly.
    /// </summary>
    internal sealed record VideosBatchRequest(string? Op, int[]? CoveIds);

    /// <summary>The per-card status batch request: the visible grid page's Cove video ids. Nullable for a clean 400.</summary>
    internal sealed record SceneStatusBatchRequest(int[]? CoveIds);

    /// <summary>The studio/performer card-badge batch request: the entity kind + the visible page's Cove ids.</summary>
    internal sealed record EntityStatusBatchRequest(string? Kind, int[]? CoveEntityIds);

    /// <summary>The studios/performers toolbar row's library-wide count: total Cove entities of the kind and how
    /// many are monitored in Whisparr.</summary>
    internal sealed record EntityLibrarySummary(int Total, int Monitored);

    /// <summary>
    /// The <c>/videos-batch</c> response: the resolved <see cref="Op"/> plus the aggregate counts —
    /// <see cref="Total"/> selected, <see cref="Succeeded"/>, <see cref="Skipped"/> (no StashDB identity, or —
    /// for a search op — not yet an added Whisparr movie), and <see cref="Failed"/>. Carries no scene id/key.
    /// </summary>
    internal sealed record VideosBatchResult(string Op, int Total, int Succeeded, int Skipped, int Failed);

    /// <summary>The videos-batch operations, parsed from the wire <c>Op</c> string by <c>TryParseBatchOp</c>.</summary>
    internal enum BatchOp
    {
        Add,
        Search,
        SearchUpgrades,
        Exclude,
    }

    /// <summary>
    /// The <c>/entities-batch</c> request body: the entity <see cref="Kind"/> (<c>"studio"</c>/<c>"performer"</c>),
    /// the selected Cove <see cref="CoveEntityIds"/> (capped before any per-item work), the
    /// <see cref="Op"/> (<c>monitor</c>/<c>unmonitor</c>/<c>addMissing</c>/<c>search</c>/<c>reflectOwned</c>,
    /// case-insensitive), and the monitor <see cref="Scope"/> (<c>NewReleases</c>/<c>AllScenes</c>, used only by
    /// monitor). No url/key and no remote ids — each entity's identity is resolved SERVER-SIDE from its Cove id.
    /// Fields are nullable so a malformed body is rejected cleanly (400).
    /// </summary>
    internal sealed record EntitiesBatchRequest(string? Kind, int[]? CoveEntityIds, string? Op, string? Scope);

    /// <summary>
    /// The <c>/entities-batch</c> response: the resolved <see cref="Op"/> plus aggregate counts —
    /// <see cref="Total"/> selected entities, <see cref="Succeeded"/> (op returned Ok), <see cref="Failed"/>, and
    /// <see cref="Skipped"/> (no identity for the connected version — no outbound call). Carries no id/key.
    /// </summary>
    internal sealed record EntitiesBatchResult(string Op, int Total, int Succeeded, int Failed, int Skipped);

    /// <summary>The entities-batch operations, parsed from the wire <c>Op</c> string by <c>TryParseEntityBatchOp</c>.</summary>
    internal enum EntityBatchOp
    {
        Monitor,
        Unmonitor,
        AddMissing,
        Search,
        ReflectOwned,
    }

    /// <summary>
    /// One Cove remote-id pair as forwarded from the entity's slot context: the metadata-server
    /// <see cref="Endpoint"/> (e.g. <c>https://stashdb.org/graphql</c>) and the entity's <see cref="RemoteId"/>
    /// on it. The handler selects the pair whose endpoint matches the stored StashDbEndpoint to obtain the
    /// Whisparr lookup id — so the StashDB-endpoint match stays a single server-side source of truth.
    /// </summary>
    internal sealed record RemoteIdInput(string? Endpoint, string? RemoteId);

    /// <summary>One reconciliation row for the UI table — a flat projection of a <see cref="MatchResult"/>.</summary>
    /// <remarks>
    /// <c>Status</c> is the bucket (<c>"matched"</c> / <c>"needsReview"</c> / <c>"unmatched"</c>); <c>MatchMethod</c>
    /// is the resolving leg (<c>"StashId"</c> / <c>"Tpdb"</c>) or null when unmatched; <c>CoveId</c>
    /// / <c>CoveTitle</c> are null when the movie matched nothing.
    /// </remarks>
    internal sealed record ReconRow(
        int WhisparrMovieId,
        string? SceneTitle,
        int? SceneYear,
        int? CoveId,
        string? CoveTitle,
        string? MatchMethod,
        string Status,
        [property: JsonConverter(typeof(SceneWhisparrStateJsonConverter))] SceneWhisparrState WhisparrState,
        bool WhisparrHasFile)
    {
        public static ReconRow From(MatchResult r, string status, IReadOnlySet<string> excludedSet) => new(
            WhisparrMovieId: r.Movie.Id,
            SceneTitle: r.Movie.Title,
            SceneYear: r.Movie.Year,
            CoveId: r.MatchedVideo?.CoveId,
            CoveTitle: r.MatchedVideo?.Title,
            MatchMethod: r.Leg?.ToString(),
            Status: status,
            // A recon row is a present Whisparr movie (always "added"). WhisparrState is the PRIMARY
            // management axis — monitored/unmonitored keyed on the row's monitored flag, or excluded when the
            // movie's StashId is in the exclusion read (camelCase wire string pinned by the converter).
            // WhisparrHasFile is the SECONDARY file fact carried alongside, never folded into the state.
            WhisparrState: SceneStatusProjector.ClassifyMovie(r.Movie, excludedSet),
            WhisparrHasFile: r.Movie.HasFile);
    }

    /// <summary>The <c>/preview-sync</c> response: the flat rows + the bucket counts.</summary>
    internal sealed record ReconResponse(IReadOnlyList<ReconRow> Rows, ReconciliationCounts Counts);

    /// <summary>The <c>/reconciliation</c> status counts over the persisted match map (by user-decision status).</summary>
    internal sealed record PersistedCounts(int Confirmed, int NeedsReview, int Rejected, int Total);

    /// <summary>The <c>/import-log</c> counts over the audit journal (by ingest result).</summary>
    internal sealed record ImportLogCounts(int Imported, int Skipped, int Flagged, int Total);

    /// <summary>The <c>/import-log</c> <c>syncHealth</c> view: unresolved path-mismatch import failures (Cove
    /// couldn't open the path Whisparr reported) since the last success — the settings banner's data source.</summary>
    internal sealed record SyncHealthView(int PathMismatch, long? LastMismatchTicks, IReadOnlyList<string> SamplePaths);
}
