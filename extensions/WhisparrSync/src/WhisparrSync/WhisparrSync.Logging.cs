using Microsoft.Extensions.Logging;
using WhisparrSync.Monitor;

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

    // WR-03: a webhook authenticated via the ?token= query string rather than the X-Cove-Token header. The
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
