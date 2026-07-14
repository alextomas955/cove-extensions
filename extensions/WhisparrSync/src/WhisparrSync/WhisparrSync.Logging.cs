using Microsoft.Extensions.Logging;

namespace WhisparrSync;

/// <summary>
/// Source-generated, high-performance log messages for the connection slice (the CA1848/CA1873 pattern:
/// each call site is a strongly-typed method with no boxing and no argument evaluation when the level is
/// disabled). Security invariant (CONN-06): NO template accepts the API key or a URL-with-embedded-key —
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

    // Logs only the outcome flag — never the webhook secret or the URL-with-token (CONN-06).
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
