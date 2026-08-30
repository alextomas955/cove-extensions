using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;

namespace WhisparrSync;

/// <summary>
/// Source-generated log messages for this extension.
/// </summary>
/// <remarks>
/// These use the <see cref="LoggerMessageAttribute"/> source generator (the pattern the analyzers
/// require, CA1848/CA1873): each call site is a strongly-typed method with no boxing and no argument
/// evaluation when the level is disabled.
/// </remarks>
public sealed partial class WhisparrSync
{
    // The host configuration is unavailable for the whole load, so everything measured from it reports
    // as unresolved until a host supplies one. Without this line that state is invisible.
    [LoggerMessage(
        EventId = 2000, Level = LogLevel.Warning,
        Message = "[WhisparrSync] the host supplied no Cove configuration; nothing measured from it can be resolved")]
    private partial void LogNoCoveConfiguration();
}

/// <summary>
/// Source-generated log messages for the services this extension registers, which are not members of
/// the extension class and so cannot declare partial methods on it.
/// </summary>
/// <remarks>
/// No template here takes an API key, and none takes an address: an address may carry credentials in
/// its user-info, and a log sink is durable and readable. A host name is the most an outbound failure
/// needs to be diagnosable, and it cannot carry one.
/// </remarks>
internal static partial class WhisparrSyncLog
{
    // A connection test that reached nothing at all. Best-effort by design — the caller turns the
    // failure into an answer for the user — so this is the one line that says a request was made and
    // died, and without it that fact is invisible in the host's log.
    [LoggerMessage(
        EventId = 2100, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a connection test to {Host} produced no response ({Failure})")]
    internal static partial void ConnectionTransportFailure(
        ILogger logger,
        ConnectionTransportFailure failure,
        string host);
}
