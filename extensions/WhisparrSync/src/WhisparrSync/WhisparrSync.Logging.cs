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
}
