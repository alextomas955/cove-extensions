using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;

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

    // A host service this extension's container could not produce. Information rather than Warning:
    // the extension loads either way and reports the reading through its own probe response, so the
    // line is the record of a load-time observation rather than a failure.
    [LoggerMessage(
        EventId = 2001, Level = LogLevel.Information,
        Message = "[WhisparrSync] IScanService could not be obtained from this extension's container")]
    private partial void LogNoScanService();

    /// <inheritdoc cref="LogNoScanService"/>
    [LoggerMessage(
        EventId = 2002, Level = LogLevel.Information,
        Message = "[WhisparrSync] IMetadataServerService could not be obtained from this extension's container")]
    private partial void LogNoMetadataServerService();
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

    // The one best-effort catch in the secret port. Losing the insert is the correct outcome — the row
    // that is there is the one every later request is authenticated against — but a mint that silently
    // did nothing is not something to leave invisible.
    [LoggerMessage(
        EventId = 2101, Level = LogLevel.Information,
        Message = "[WhisparrSync] a concurrent mint had already stored the callback secret; the stored one is in use")]
    internal static partial void ConcurrentMintLostToAnExistingRow(ILogger logger);

    // A registration the instance accepted whose effect a re-read did not find. No address here: an
    // address may carry credentials in its user-info, and the status plus the generation is what makes
    // this diagnosable.
    [LoggerMessage(
        EventId = 2102, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a callback registration on {Generation} answered {WriteStatus} and the notification read back differently")]
    internal static partial void CallbackRegistrationDidNotTake(
        ILogger logger,
        WhisparrGeneration generation,
        int writeStatus);

    // An ingest that registered nothing, named by cause. No path here: a refused delivery's path is a
    // caller-supplied string and a log sink is durable and readable.
    [LoggerMessage(
        EventId = 2103, Level = LogLevel.Information,
        Message = "[WhisparrSync] an import from {Generation} registered nothing ({Outcome})")]
    internal static partial void ImportRefused(
        ILogger logger,
        WhisparrGeneration generation,
        ImportOutcome outcome);

    // An event type this product does not act on. Named so an instance sending one nobody expected is
    // visible, and emitted once per distinct type rather than once per delivery: a subscribed trigger
    // that fires often would otherwise fill the log with one line per file.
    [LoggerMessage(
        EventId = 2104, Level = LogLevel.Information,
        Message = "[WhisparrSync] {Generation} sent the event type {EventType}, which this product does not act on")]
    internal static partial void ImportEventTypeIgnored(
        ILogger logger,
        WhisparrGeneration generation,
        string eventType);

    // A root-folder read that reached nothing. Best-effort by design - the ingest refuses on the
    // empty reading - so this is the one line that says the request was made and died.
    [LoggerMessage(
        EventId = 2105, Level = LogLevel.Warning,
        Message = "[WhisparrSync] reading {Generation}'s declared root folders from {Host} produced no response")]
    internal static partial void ReportedRootReadFailed(
        ILogger logger,
        WhisparrGeneration generation,
        string host);
}
