using System.Text.Json;
using System.Text.Json.Serialization;
using WhisparrSync.Monitor;

namespace WhisparrSync.Options;

/// <summary>
/// The persisted Whisparr Sync configuration, serialized as a single forward-compatible
/// <c>System.Text.Json</c> blob under the <c>"options"</c> store key (unknown props ignored on load,
/// missing props default). Mostly scalar, so the compiler-generated value equality is right for the
/// scalar fields; the one collection field (<see cref="TagsOnAdd"/>) is compared by reference under record
/// equality (collections are not structurally compared), so equality is NOT used to detect option changes.
/// </summary>
/// <remarks>
/// <see cref="ApiKey"/> and <see cref="WebhookSecret"/> live at rest in Cove's plaintext
/// key-value store. The key is used server-side only — it is projected out of every response through
/// <see cref="OptionsView"/> (which exposes a <c>hasApiKey</c> boolean instead) and never appears in a log.
/// </remarks>
public sealed record WhisparrOptions
{
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string SelectedVersion { get; init; } = "v3";
    public string DetectedVersion { get; init; } = "";
    public int QualityProfileId { get; init; }
    public string WebhookSecret { get; init; } = "";

    /// <summary>
    /// The last-saved connection per Whisparr version (keyed "v3"/"v2"), so switching the version selector in
    /// Settings restores that version's URL / key / root / quality profile instead of blanking them — a Cove user
    /// can toggle between a v3 and a v2 instance without re-entering either. The top-level
    /// <see cref="BaseUrl"/>/<see cref="ApiKey"/>/<see cref="QualityProfileId"/> remain
    /// the ACTIVE resolved connection (the one <c>ResolveCredsAsync</c> uses) — this map is the per-version memory
    /// behind it, updated in lockstep on every save.
    /// </summary>
    public IReadOnlyDictionary<string, WhisparrConnection> SavedConnections { get; init; }
        = new Dictionary<string, WhisparrConnection>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Extra Whisparr tag labels applied to every Cove-initiated add, IN ADDITION to the always-present
    /// <c>cove-sync</c> origin tag (which is applied unconditionally and is NOT listed here). Each label is
    /// resolved to a Whisparr tag id (found-or-created) before the add. Defaults to empty so an add carries
    /// only the origin tag unless the user opts into more; the design's default <c>cove</c> chip is a UI
    /// concern, not a backend default (a non-empty default here would force a tag round-trip on every add).
    /// </summary>
    public IReadOnlyList<string> TagsOnAdd { get; init; } = [];

    /// <summary>
    /// The default monitored state for a Cove-initiated scene add ("Monitor new items by default").
    /// Feeds <c>AddSceneAsync</c>'s monitored choice; the origin tag and <c>searchForMovie:false</c> are
    /// applied regardless (an add never grabs).
    /// </summary>
    public bool MonitorNewByDefault { get; init; } = true;

    /// <summary>
    /// How far a "Monitor in Whisparr" toggle cascades by default when the caller does not specify a scope.
    /// </summary>
    /// <remarks>
    /// Persisted as a string ("NewReleases"/"AllScenes") so a reorder of <see cref="MonitorScope"/> cannot
    /// silently repoint a stored blob. Defaults to <see cref="MonitorScope.NewReleases"/> — the loop-safest
    /// default that never registers owned back-catalogue as missing-in-Whisparr. Both scopes keep the add
    /// non-grabbing; only an explicit search grabs.
    /// </remarks>
    [JsonConverter(typeof(JsonStringEnumConverter<MonitorScope>))]
    public MonitorScope DefaultMonitorScope { get; init; } = MonitorScope.NewReleases;

    /// <summary>
    /// The design's "Search on add" default. Persisted for the settings UI, but DELIBERATELY NOT
    /// wired to an auto-search: the locked loop-safety invariant forbids an add from grabbing, so this flag
    /// never triggers a command here (only the explicit Search / Search-for-upgrades / grab verbs grab).
    /// </summary>
    public bool SearchOnAdd { get; init; }

    /// <summary>
    /// The default for the "Allow quality upgrades" toggle. Consumed by the upgrades verb: when
    /// off, a search-for-upgrades is a no-op that issues no command; when on, the verb posts the upgrade search.
    /// </summary>
    public bool AllowQualityUpgrades { get; init; } = true;

    /// <summary>
    /// The Cove metadata-server GraphQL URL whose <c>VideoRemoteId</c>s are the StashDB match key.
    /// A configurable URL, NOT a fixed literal: Cove stores each metadata server's own
    /// endpoint (ThePornDB — <c>https://theporndb.net/graphql</c> — shares the identical field), so the
    /// reconciliation filters remote ids on THIS value case-insensitively. A plain non-secret setting.
    /// </summary>
    public string StashDbEndpoint { get; init; } = "https://stashdb.org/graphql";

    /// <summary>
    /// The ThePornDB metadata-server GraphQL URL whose <c>VideoRemoteId</c>s key the outward target on a
    /// Whisparr v2 (Sonarr) instance. Mirrors <see cref="StashDbEndpoint"/> (a configurable URL, matched
    /// case-insensitively), because a v2 site is addressed by its TPDB id, not a StashDB id. A plain non-secret
    /// setting; advanced / not surfaced in the settings UI (like <see cref="StashDbEndpoint"/>).
    /// </summary>
    public string TpdbEndpoint { get; init; } = "https://theporndb.net/graphql";

    /// <summary>
    /// Cove→Whisparr path-prefix rewrites for the Docker-vs-local deployment, applied before a Cove file path is
    /// prefix-matched against Whisparr's own root list. Empty (the default) is identity — the two see the library
    /// at the same mount. A plain non-secret setting, modeled on <see cref="StashDbEndpoint"/>/<see cref="TpdbEndpoint"/>;
    /// the Cove-side analogue of Whisparr's own Remote Path Mapping (the translation is applied here, not written
    /// into Whisparr).
    /// </summary>
    public IReadOnlyList<PathTranslationRule> PathTranslation { get; init; } = [];

    /// <summary>
    /// The metadata endpoint whose remote id identifies the outward Whisparr target for the CONNECTED version.
    /// </summary>
    /// <remarks>
    /// The match key MUST follow the connected version: v3 (Eros) resolves a Cove entity by its StashDB id,
    /// v2 (Sonarr) by its ThePornDB id. Resolving by the wrong endpoint would point a mutation at an id the
    /// connected instance cannot know. Case-insensitive on <see cref="SelectedVersion"/>; defaults to the
    /// StashDB endpoint for any non-v2 selection (v3 is the only other supported version).
    /// </remarks>
    public string IdentityEndpoint
        => string.Equals(SelectedVersion, "v2", StringComparison.OrdinalIgnoreCase) ? TpdbEndpoint : StashDbEndpoint;

    /// <summary>
    /// Shared serializer settings for the load/save round-trip: case-insensitive property names so a
    /// hand-edited blob still binds. <c>OptionsStore</c> reuses this exact instance.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Returns a copy with the user-submitted settings applied. Write-only key semantics: an empty or
    /// absent submitted <paramref name="apiKey"/> PRESERVES the stored key (the UI never echoes
    /// the key back, so a blank submission means "unchanged", never "clear it"); a non-empty value replaces
    /// it. A null <paramref name="baseUrl"/>/<paramref name="selectedVersion"/> likewise preserves the
    /// stored value.
    /// </summary>
    public WhisparrOptions WithSubmitted(
        string? baseUrl, string? apiKey, string? selectedVersion, int qualityProfileId,
        string? stashDbEndpoint = null,
        string? tpdbEndpoint = null,
        IReadOnlyList<PathTranslationRule>? pathTranslation = null,
        IReadOnlyList<string>? tagsOnAdd = null,
        bool? monitorNewByDefault = null,
        bool? searchOnAdd = null,
        bool? allowQualityUpgrades = null,
        MonitorScope? defaultMonitorScope = null)
    {
        var effectiveVersion = string.IsNullOrWhiteSpace(selectedVersion) ? SelectedVersion : selectedVersion;
        var effectiveUrl = baseUrl ?? BaseUrl;

        // Write-only key, per version: a non-blank submission replaces it. A blank submission preserves the key
        // SAVED FOR THIS version — the stored connection when the version has one, else the active key only when
        // the version is unchanged (a first-ever save / same instance). Switching to a not-yet-configured version
        // with a blank key resolves to no key, never the other version's — so a toggle can't leak v3's key to v2.
        string effectiveKey;
        if (!string.IsNullOrEmpty(apiKey))
        {
            effectiveKey = apiKey;
        }
        else if (SavedConnections.TryGetValue(effectiveVersion, out var saved))
        {
            effectiveKey = saved.ApiKey;
        }
        else
        {
            effectiveKey = string.Equals(effectiveVersion, SelectedVersion, StringComparison.OrdinalIgnoreCase)
                ? ApiKey
                : "";
        }

        var connection = new WhisparrConnection(effectiveUrl, effectiveKey, qualityProfileId);
        var savedConnections = new Dictionary<string, WhisparrConnection>(SavedConnections, StringComparer.OrdinalIgnoreCase)
        {
            [effectiveVersion] = connection,
        };

        return this with
        {
            BaseUrl = connection.BaseUrl,
            ApiKey = connection.ApiKey,
            SelectedVersion = effectiveVersion,
            QualityProfileId = qualityProfileId,
            SavedConnections = savedConnections,
            // Preserve-on-blank like the other string fields: a null/blank submission keeps the stored endpoint.
            StashDbEndpoint = string.IsNullOrWhiteSpace(stashDbEndpoint) ? StashDbEndpoint : stashDbEndpoint,
            TpdbEndpoint = string.IsNullOrWhiteSpace(tpdbEndpoint) ? TpdbEndpoint : tpdbEndpoint,
            // Preserve-on-null: an absent PathTranslation keeps the stored table (a partial save never wipes it).
            PathTranslation = pathTranslation ?? PathTranslation,
            // The add-defaults preserve-on-null so a partial save never resets an unrelated toggle.
            TagsOnAdd = tagsOnAdd ?? TagsOnAdd,
            MonitorNewByDefault = monitorNewByDefault ?? MonitorNewByDefault,
            SearchOnAdd = searchOnAdd ?? SearchOnAdd,
            AllowQualityUpgrades = allowQualityUpgrades ?? AllowQualityUpgrades,
            DefaultMonitorScope = defaultMonitorScope ?? DefaultMonitorScope,
        };
    }
}

/// <summary>
/// One saved Whisparr connection — the URL, API key, and the instance-specific quality-profile id for one
/// version. Held per version in <see cref="WhisparrOptions.SavedConnections"/> so switching versions
/// in Settings restores a whole connection at once. The key lives at rest exactly like
/// <see cref="WhisparrOptions.ApiKey"/> (Cove's plaintext options blob) and is dropped from the redaction-safe
/// <see cref="ConnectionView"/> the UI receives.
/// </summary>
public sealed record WhisparrConnection(string BaseUrl, string ApiKey, int QualityProfileId);

/// <summary>
/// One Cove→Whisparr path-prefix rewrite. A Cove file path at or beneath <see cref="CovePrefix"/> is rewritten
/// with <see cref="WhisparrPrefix"/> before it is prefix-matched against Whisparr's root list, so a
/// containerized Whisparr that mounts the same library at a different path still resolves the right root.
/// </summary>
public sealed record PathTranslationRule(string CovePrefix, string WhisparrPrefix);

/// <summary>
/// The redaction-safe projection of <see cref="WhisparrOptions"/> returned to the settings UI: it carries
/// every persisted field EXCEPT the API key, replacing it with a <c>hasApiKey</c> boolean. No
/// response ever serializes the raw key.
/// </summary>
public sealed record OptionsView(
    string BaseUrl,
    string SelectedVersion,
    string DetectedVersion,
    int QualityProfileId,
    [property: JsonPropertyName("hasApiKey")] bool HasApiKey,
    IReadOnlyList<string> TagsOnAdd,
    bool MonitorNewByDefault,
    bool AllowQualityUpgrades,
    string TpdbEndpoint,
    [property: JsonConverter(typeof(JsonStringEnumConverter<MonitorScope>))] MonitorScope DefaultMonitorScope,
    IReadOnlyDictionary<string, ConnectionView> SavedConnections)
{
    /// <summary>
    /// Projects the stored options to the redaction-safe view (the raw key is dropped here). The active
    /// version's connection is folded into <see cref="SavedConnections"/> even if it predates per-version
    /// storage, so the UI always has the current connection to restore when the user toggles back to it.
    /// </summary>
    public static OptionsView From(WhisparrOptions options)
    {
        var connections = options.SavedConnections.ToDictionary(
            kv => kv.Key, kv => ConnectionView.From(kv.Value), StringComparer.OrdinalIgnoreCase);
        if (!connections.ContainsKey(options.SelectedVersion))
        {
            connections[options.SelectedVersion] = new ConnectionView(
                options.BaseUrl, options.QualityProfileId,
                HasApiKey: !string.IsNullOrEmpty(options.ApiKey));
        }

        return new(options.BaseUrl, options.SelectedVersion, options.DetectedVersion,
            options.QualityProfileId, HasApiKey: !string.IsNullOrEmpty(options.ApiKey),
            TagsOnAdd: options.TagsOnAdd, MonitorNewByDefault: options.MonitorNewByDefault,
            AllowQualityUpgrades: options.AllowQualityUpgrades, TpdbEndpoint: options.TpdbEndpoint,
            DefaultMonitorScope: options.DefaultMonitorScope, SavedConnections: connections);
    }
}

/// <summary>One saved connection as the UI sees it: the URL + quality-profile id + a <c>hasApiKey</c> flag; the raw
/// key is never projected. The UI shows the URL/profile on a version toggle and a "key is set" badge.</summary>
public sealed record ConnectionView(
    string BaseUrl,
    int QualityProfileId,
    [property: JsonPropertyName("hasApiKey")] bool HasApiKey)
{
    public static ConnectionView From(WhisparrConnection connection)
        => new(connection.BaseUrl, connection.QualityProfileId,
            HasApiKey: !string.IsNullOrEmpty(connection.ApiKey));
}
