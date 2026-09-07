using System.Text.Json;
using System.Text.Json.Serialization;
using WhisparrSync.Contracts;

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

    /// <summary>When <see cref="DetectedVersion"/> was detected, in UTC ticks; zero means never.</summary>
    /// <remarks>
    /// Stamped only by a successful probe against the stored host, in the same save as the version itself, so
    /// the two can never diverge. It is NOT a reachability time: the health record's acquisition last-healthy
    /// tick is also advanced by the ambient roots read and the fifteen-minute heartbeat, so borrowing it here
    /// would report a version as freshly verified when it was last actually detected days earlier.
    /// </remarks>
    public long DetectedVersionTicks { get; init; }

    public string WebhookSecret { get; init; } = "";

    /// <summary>
    /// The last-saved connection per Whisparr version (keyed "v3"/"v2"), so switching the version selector in
    /// Settings restores that version's URL and key instead of blanking them — a Cove user
    /// can toggle between a v3 and a v2 instance without re-entering either. The top-level
    /// <see cref="BaseUrl"/>/<see cref="ApiKey"/> remain
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
    /// The monitored state for a Cove-initiated scene add ("Monitor new items by default").
    /// Feeds <c>AddSceneAsync</c>'s monitored choice; the origin tag and <c>searchForMovie:false</c> are
    /// applied regardless (an add never grabs).
    /// </summary>
    /// <remarks>
    /// Defaults OFF. Whisparr treats a monitored movie with no file as WANTED and grabs it on the next RSS
    /// sweep, and every scene added here is one Cove already holds the file for — so monitoring before the
    /// file is attached asks Whisparr for a duplicate. Safe to enable once the owned file has been
    /// reflected into Whisparr (<c>hasFile</c> true).
    /// </remarks>
    public bool MonitorNewByDefault { get; init; }

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
    /// The persisted webhook origin (scheme + host + port) the read endpoint falls back to BEFORE a Whisparr
    /// connector exists, so a pre-registration edit survives a settings refresh instead of reverting to the
    /// browser-derived request host. Empty on first run (no persisted host → the read derives the default from
    /// the request). Once a connector is registered the connector's own <c>url</c> is authoritative and this
    /// seeds only the first-run / pre-registration default. A plain non-secret setting, modeled on
    /// <see cref="StashDbEndpoint"/>/<see cref="TpdbEndpoint"/> (preserve-on-blank).
    /// </summary>
    public string WebhookHost { get; init; } = "";

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
        string? baseUrl, string? apiKey, string? selectedVersion,
        string? stashDbEndpoint = null,
        string? tpdbEndpoint = null,
        string? webhookHost = null,
        IReadOnlyList<PathTranslationRule>? pathTranslation = null,
        IReadOnlyList<string>? tagsOnAdd = null,
        bool? monitorNewByDefault = null,
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

        // The detected version is stamped only against the stored host, so it is a fact about that ADDRESS and stays
        // true when only the selector moves; a save that repoints the address would leave it describing an instance
        // this connection no longer reaches. The comparison is deliberately the same trimmed, case-insensitive rule
        // PersistDetectedVersionAsync pairs a host by, so the write gate and this one cannot disagree.
        var sameHost = string.Equals(
            effectiveUrl.TrimEnd('/'), BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        var connection = new WhisparrConnection(effectiveUrl, effectiveKey);
        var savedConnections = new Dictionary<string, WhisparrConnection>(SavedConnections, StringComparer.OrdinalIgnoreCase)
        {
            [effectiveVersion] = connection,
        };

        return this with
        {
            BaseUrl = connection.BaseUrl,
            ApiKey = connection.ApiKey,
            SelectedVersion = effectiveVersion,
            SavedConnections = savedConnections,
            DetectedVersion = sameHost ? DetectedVersion : "",
            DetectedVersionTicks = sameHost ? DetectedVersionTicks : 0,
            // Preserve-on-blank like the other string fields: a null/blank submission keeps the stored endpoint.
            StashDbEndpoint = string.IsNullOrWhiteSpace(stashDbEndpoint) ? StashDbEndpoint : stashDbEndpoint,
            TpdbEndpoint = string.IsNullOrWhiteSpace(tpdbEndpoint) ? TpdbEndpoint : tpdbEndpoint,
            WebhookHost = string.IsNullOrWhiteSpace(webhookHost) ? WebhookHost : webhookHost,
            // Preserve-on-null: an absent PathTranslation keeps the stored table (a partial save never wipes it).
            PathTranslation = pathTranslation ?? PathTranslation,
            // The add-defaults preserve-on-null so a partial save never resets an unrelated toggle.
            TagsOnAdd = tagsOnAdd ?? TagsOnAdd,
            MonitorNewByDefault = monitorNewByDefault ?? MonitorNewByDefault,
            AllowQualityUpgrades = allowQualityUpgrades ?? AllowQualityUpgrades,
            DefaultMonitorScope = defaultMonitorScope ?? DefaultMonitorScope,
        };
    }
}

/// <summary>
/// One saved Whisparr connection — the URL and API key for one version. Held per version in
/// <see cref="WhisparrOptions.SavedConnections"/> so switching versions in Settings restores a whole connection
/// at once. The key lives at rest exactly like <see cref="WhisparrOptions.ApiKey"/> (Cove's plaintext options
/// blob) and is dropped from the redaction-safe <see cref="ConnectionView"/> the UI receives.
/// </summary>
public sealed record WhisparrConnection(string BaseUrl, string ApiKey);

/// <summary>
/// One Cove→Whisparr path-prefix rewrite. A Cove file path at or beneath <see cref="CovePrefix"/> is rewritten
/// with <see cref="WhisparrPrefix"/> before it is prefix-matched against Whisparr's root list, so a
/// containerized Whisparr that mounts the same library at a different path still resolves the right root.
/// </summary>
public sealed record PathTranslationRule(string CovePrefix, string WhisparrPrefix);

/// <summary>
/// The redaction-safe projection of <see cref="WhisparrOptions"/> returned to the settings UI: it carries
/// every persisted field EXCEPT the API key, which it replaces with a <c>hasApiKey</c> boolean. No response
/// ever serializes a raw key.
/// </summary>
/// <remarks>
/// <see cref="DetectedVersionTicks"/> is nullable because the stored record uses zero for never, and a client
/// rendering the epoch would state a time that never happened.
/// </remarks>
public sealed record OptionsView(
    string BaseUrl,
    string SelectedVersion,
    string DetectedVersion,
    long? DetectedVersionTicks,
    bool HasApiKey,
    IReadOnlyList<string> TagsOnAdd,
    bool MonitorNewByDefault,
    bool AllowQualityUpgrades,
    string TpdbEndpoint,
    string WebhookHost,
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
                options.BaseUrl, HasApiKey: !string.IsNullOrEmpty(options.ApiKey));
        }

        return new(options.BaseUrl, options.SelectedVersion, options.DetectedVersion,
            DetectedVersionTicks: options.DetectedVersionTicks > 0 ? options.DetectedVersionTicks : null,
            HasApiKey: !string.IsNullOrEmpty(options.ApiKey),
            TagsOnAdd: options.TagsOnAdd, MonitorNewByDefault: options.MonitorNewByDefault,
            AllowQualityUpgrades: options.AllowQualityUpgrades, TpdbEndpoint: options.TpdbEndpoint,
            WebhookHost: options.WebhookHost,
            SavedConnections: connections);
    }
}

/// <summary>One saved connection as the UI sees it: the URL + a <c>hasApiKey</c> flag; the raw
/// key is never projected. The UI shows the URL on a version toggle and a "key is set" badge.</summary>
public sealed record ConnectionView(
    string BaseUrl,
    bool HasApiKey)
{
    public static ConnectionView From(WhisparrConnection connection)
        => new(connection.BaseUrl, HasApiKey: !string.IsNullOrEmpty(connection.ApiKey));
}
