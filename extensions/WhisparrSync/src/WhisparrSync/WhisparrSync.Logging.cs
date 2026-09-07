using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;

namespace WhisparrSync;

/// <summary>
/// Source-generated, high-performance log messages for the connection slice (the CA1848/CA1873 pattern:
/// each call site is a strongly-typed method with no boxing and no argument evaluation when the level is
/// disabled). Security invariant: NO template accepts the API key or a URL-with-embedded-key —
/// only the detected version/instance name and a short unreachable reason are ever logged.
/// </summary>
public sealed partial class WhisparrSync
{
    [LoggerMessage(
        EventId = 2000, Level = LogLevel.Information,
        Message = "[WhisparrSync] connection tested: Whisparr {Version} ({InstanceName})")]
    private partial void LogConnectTested(string version, string instanceName);

    [LoggerMessage(
        EventId = 2001, Level = LogLevel.Warning,
        Message = "[WhisparrSync] Whisparr not reachable/usable: {Reason}")]
    private partial void LogWhisparrUnreachable(string reason);

    [LoggerMessage(
        EventId = 2002, Level = LogLevel.Warning,
        Message = "[WhisparrSync] refused connection: detected Whisparr major version {DetectedMajor}, only v3 is supported")]
    private partial void LogVersionRefused(int detectedMajor);

    // Logs only the outcome flag — never the webhook secret or the URL-with-token.
    [LoggerMessage(
        EventId = 2003, Level = LogLevel.Information,
        Message = "[WhisparrSync] webhook auto-register attempted: registered={Registered}")]
    private partial void LogWebhookRegistered(bool registered);

    // The reconcile loop is a best-effort backstop; a fault stops it (webhook stays primary) but must never
    // crash the host. Logs the terse reason only — never a path or secret.
    [LoggerMessage(
        EventId = 2004, Level = LogLevel.Warning,
        Message = "[WhisparrSync] reconcile loop stopped on an unexpected fault: {Reason}")]
    private partial void LogReconcileLoopFault(string reason);

    // Distinct from 2004: there the loop ended, here one tick was lost and the next retries. Nothing else
    // records that lost tick. Logs the terse reason only — never a path or secret.
    [LoggerMessage(
        EventId = 2033, Level = LogLevel.Warning,
        Message = "[WhisparrSync] reconcile enqueue failed for this tick; the next tick retries: {Reason}")]
    private partial void LogReconcileEnqueueFault(string reason);

    // A webhook authenticated via the ?token= query string rather than the X-Cove-Token header. The
    // secret in a URL query is routinely captured by Kestrel/proxy/access logs, so the header is preferred
    // (auto-register uses it). The message never contains the secret itself. Guarded to warn ONCE per process
    // (WarnQueryTokenChannelOnce) so a busy webhook does not spam the log.
    [LoggerMessage(
        EventId = 2005, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a webhook authenticated via the ?token= query string; that secret can be "
                + "captured by reverse-proxy/access logs. Prefer the X-Cove-Token header (Register in Whisparr "
                + "configures it automatically).")]
    private partial void LogWebhookTokenInQuery();

    // Records a studio/performer monitor toggle: the entity kind, the requested on/off state,
    // and whether this call created the entity in Whisparr. Logs no id, no host, and never the API key.
    [LoggerMessage(
        EventId = 2006, Level = LogLevel.Information,
        Message = "[WhisparrSync] monitor toggled: kind={Kind} monitored={Monitored} added={Added}")]
    private partial void LogMonitorToggled(EntityKind kind, bool monitored, bool added);

    // Records a read of the library-wide scene-status summary: logs ONLY the number of scenes
    // classified — never a scene id/title/path, the Whisparr host, or the API key.
    [LoggerMessage(
        EventId = 2007, Level = LogLevel.Information,
        Message = "[WhisparrSync] scene-status summary read: {Total} scenes classified")]
    private partial void LogSceneStatusRead(int total);

    // Records a per-entity discovery read: logs ONLY the missing-scene count — never a scene/entity id or
    // title, the Whisparr host, or the API key.
    [LoggerMessage(
        EventId = 2018, Level = LogLevel.Information,
        Message = "[WhisparrSync] discovery read: {Missing} missing scenes")]
    private partial void LogDiscoveryRead(int missing);

    // Records a direct StashDB catalogue read for an unmonitored studio: logs ONLY the scene count returned —
    // never the StashDB key, a scene id/title, or the endpoint.
    [LoggerMessage(
        EventId = 2023, Level = LogLevel.Information,
        Message = "[WhisparrSync] StashDB catalogue read: {Scenes} scenes")]
    private partial void LogStashDbCatalogueRead(int scenes);

    // Records a direct ThePornDB catalogue read for an unmonitored v2 site: logs ONLY the scene count returned —
    // never the TPDB token, a scene id/title, or the endpoint.
    [LoggerMessage(
        EventId = 2024, Level = LogLevel.Information,
        Message = "[WhisparrSync] ThePornDB catalogue read: {Scenes} scenes")]
    private partial void LogTpdbCatalogueRead(int scenes);

    // Records a whole-set facet aggregate that did not answer: logs ONLY the classified state — never the box key,
    // an entity id, or the endpoint. The page still renders; its option lists then come from the rendered rows and
    // the control says so.
    [LoggerMessage(
        EventId = 2030, Level = LogLevel.Warning,
        Message = "[WhisparrSync] facet aggregate unavailable: {State}")]
    private partial void LogFacetAggregateUnavailable(string state);

    // Records a per-scene discovery mark-wanted action: ONLY the number of scenes added monitored — never a
    // scene/entity id or title, the Whisparr host, or the API key.
    [LoggerMessage(
        EventId = 2025, Level = LogLevel.Information,
        Message = "[WhisparrSync] discovery monitor action: succeeded={Succeeded}")]
    private partial void LogDiscoveryAction(int succeeded);

    // Records a BULK discovery action (the selection or a whole-entity run): ONLY the op + the aggregate
    // count of scenes acted on — never a scene/entity id or title, the Whisparr host, or the API key.
    [LoggerMessage(
        EventId = 2026, Level = LogLevel.Information,
        Message = "[WhisparrSync] discovery bulk action: op={Op} succeeded={Succeeded}")]
    private partial void LogDiscoveryActionAll(DiscoverySceneOp op, int succeeded);

    // Records a per-scene discovery unmonitor action: ONLY whether a real Whisparr movie was flipped
    // monitored:false (false = the scene was not an added movie, a safe no-op). No id/title/host/key.
    [LoggerMessage(
        EventId = 2027, Level = LogLevel.Information,
        Message = "[WhisparrSync] discovery unmonitor action: unmonitored={Unmonitored}")]
    private partial void LogDiscoveryUnmonitorAction(bool unmonitored);

    // Records a per-scene discovery search action (the sole immediate grab): ONLY whether a search command was
    // issued (false when the scene is not yet an added Whisparr movie). No scene/entity id or title, host, or key.
    [LoggerMessage(
        EventId = 2028, Level = LogLevel.Information,
        Message = "[WhisparrSync] discovery search action: searched={Searched}")]
    private partial void LogDiscoverySearchAction(bool searched);

    // Records a read of the activity History projection: logs ONLY the number of rows returned — never a
    // scene title/id, the Whisparr host, or the API key.
    [LoggerMessage(
        EventId = 2019, Level = LogLevel.Information,
        Message = "[WhisparrSync] activity history read: {Rows} rows")]
    private partial void LogActivityHistoryRead(int rows);

    // Records a failed best-effort activity read (history / queue / wanted): the surface name + the safe failure
    // discriminator ONLY (never the Whisparr host, the API key, or a raw upstream reason) — the outage the UI
    // renders as an error state. One line for all three activity reads.
    [LoggerMessage(
        EventId = 2020, Level = LogLevel.Warning,
        Message = "[WhisparrSync] activity {Surface} read failed: {Reason}")]
    private partial void LogActivityReadFailed(string surface, string reason);

    // Records a read of the activity Queue projection: logs ONLY the number of rows returned — never a scene
    // title/id, the Whisparr host, or the API key.
    [LoggerMessage(
        EventId = 2021, Level = LogLevel.Information,
        Message = "[WhisparrSync] activity queue read: {Rows} rows")]
    private partial void LogActivityQueueRead(int rows);

    // Records a read of the activity Wanted projection: logs ONLY the number of rows returned — never a scene
    // title/id, the Whisparr host, or the API key.
    [LoggerMessage(
        EventId = 2022, Level = LogLevel.Information,
        Message = "[WhisparrSync] activity wanted read: {Rows} rows")]
    private partial void LogActivityWantedRead(int rows);

    // Records a per-scene push: whether this call created the movie and its resulting monitor
    // state. Logs no scene id/title/path, no Whisparr host, and never the API key.
    [LoggerMessage(
        EventId = 2008, Level = LogLevel.Information,
        Message = "[WhisparrSync] scene pushed: added={Added} monitored={Monitored}")]
    private partial void LogScenePushed(bool added, bool monitored);

    // Records a per-scene "search now": only whether a search command was issued (false when the scene
    // is not yet an added Whisparr movie). Logs no id/title/path/host/key.
    [LoggerMessage(
        EventId = 2009, Level = LogLevel.Information,
        Message = "[WhisparrSync] scene search issued: searched={Searched}")]
    private partial void LogSceneSearched(bool searched);

    // Records a bulk entity action (add-all-missing / search-all-monitored): the entity kind
    // and the total/succeeded/failed counts ONLY — never a scene id/title/path, the host, or the API key.
    [LoggerMessage(
        EventId = 2010, Level = LogLevel.Information,
        Message = "[WhisparrSync] bulk scene action: kind={Kind} total={Total} succeeded={Succeeded} failed={Failed}")]
    private partial void LogBulkAction(EntityKind kind, int total, int succeeded, int failed);

    // Records a scene exclusion toggle: only whether the scene was excluded or un-excluded.
    // Logs no scene id/title/path, no Whisparr host, and never the API key.
    [LoggerMessage(
        EventId = 2011, Level = LogLevel.Information,
        Message = "[WhisparrSync] scene exclusion toggled: excluded={Excluded}")]
    private partial void LogSceneExclusionToggled(bool excluded);

    // Records a read of a scene's pickable releases: ONLY the number of releases returned — never a
    // release guid/title, the Whisparr host, or the API key.
    [LoggerMessage(
        EventId = 2012, Level = LogLevel.Information,
        Message = "[WhisparrSync] scene releases listed: {Count} releases")]
    private partial void LogSceneReleasesListed(int count);

    // Records a per-scene upgrade search: only whether a search command was issued (false when the
    // scene is not yet an added Whisparr movie, or when upgrades are disabled). Logs no id/host/key.
    [LoggerMessage(
        EventId = 2013, Level = LogLevel.Information,
        Message = "[WhisparrSync] scene upgrade search issued: searched={Searched}")]
    private partial void LogSceneUpgradeSearched(bool searched);

    // Records an interactive release grab: the OUTCOME only — deliberately carries no argument, so the
    // release guid/indexer, the scene id, the Whisparr host, and the API key are all absent from the log.
    [LoggerMessage(
        EventId = 2014, Level = LogLevel.Information,
        Message = "[WhisparrSync] interactive release grabbed")]
    private partial void LogSceneReleaseGrabbed();

    // Records a videos-list batch op: the op name + the aggregate counts ONLY — never a scene id/title/
    // path, the Whisparr host, or the API key.
    [LoggerMessage(
        EventId = 2015, Level = LogLevel.Information,
        Message = "[WhisparrSync] videos batch: op={Op} total={Total} succeeded={Succeeded} skipped={Skipped} failed={Failed}")]
    private partial void LogVideosBatch(string op, int total, int succeeded, int skipped, int failed);

    // Records the whole-library "Sync my library to Whisparr" job outcome: the aggregate unit counts across
    // studios/performers/scenes ONLY — never an entity/scene id/title/path, the Whisparr host, or the API key.
    [LoggerMessage(
        EventId = 2016, Level = LogLevel.Information,
        Message = "[WhisparrSync] library sync: total={Total} succeeded={Succeeded} failed={Failed} skipped={Skipped}")]
    private partial void LogSyncLibrary(int total, int succeeded, int failed, int skipped);

    // Records a failed best-effort enrichment (metadata-server identify): the coveId + endpoint ONLY —
    // never a scene title/path, the Whisparr host, or the API key.
    [LoggerMessage(
        EventId = 2017, Level = LogLevel.Warning,
        Message = "[WhisparrSync] scene enrichment failed: coveId={CoveId} endpoint={Endpoint} (scene linked but not scraped)")]
    internal static partial void LogSceneEnrichmentFailed(ILogger logger, int coveId, string endpoint, Exception ex);

    // Records a health write that could not be persisted: the dependency key + the store's own failure text
    // ONLY — never the Whisparr host, the API key or the webhook secret, none of which the health record ever
    // holds. The text rides as a PARAMETER, so a newline inside it cannot forge a second log entry.
    [LoggerMessage(
        EventId = 2032, Level = LogLevel.Warning,
        Message = "[WhisparrSync] health record write failed for {Dependency}: {Reason}")]
    private partial void LogHealthRecordFailed(string dependency, string reason);

    // Warn at most once per process that the insecure query-token channel was used (called from the webhook
    // wiring in WhisparrSync.Api.cs). Interlocked makes the one-time guard safe under concurrent requests.
    private int _warnedQueryTokenChannel;

    internal void WarnQueryTokenChannelOnce()
    {
        if (Interlocked.Exchange(ref _warnedQueryTokenChannel, 1) == 0)
        {
            LogWebhookTokenInQuery();
        }
    }
}
