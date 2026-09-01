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

    // An ingest that registered nothing, named by cause and by the root the refusal is counted
    // against. The root and not the offending path: a refused delivery's path is a caller-supplied
    // string and a log sink is durable and readable, while a root comes from the configured
    // instance's own answer and is what a user has to go and look at.
    [LoggerMessage(
        EventId = 2103, Level = LogLevel.Information,
        Message = "[WhisparrSync] an import from {Generation} under {Root} registered nothing ({Outcome})")]
    internal static partial void ImportRefused(
        ILogger logger,
        WhisparrGeneration generation,
        ImportOutcome outcome,
        string root);

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

    // A backstop pass that imported from nothing it read, named by cause. The pass runs with nobody
    // watching and leaves the stored mark where it was, so without this line a channel that has
    // refused every pass since an instance changed looks the same as one with nothing to do.
    [LoggerMessage(
        EventId = 2107, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a backstop pass over {Generation} at {Host} imported nothing and kept its place ({Outcome})")]
    internal static partial void BackstopPassRefused(
        ILogger logger,
        WhisparrGeneration generation,
        BackstopPassOutcome outcome,
        string host);

    // A pass that ended in a failure the pass itself does not classify. Contained so the worker
    // survives it, and reported because a backstop that has silently stopped passing looks from
    // outside exactly like one with nothing to do.
    [LoggerMessage(
        EventId = 2108, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a backstop pass ended in an unexpected failure; the next wake tries again")]
    internal static partial void BackstopPassFaulted(ILogger logger, Exception failure);

    // The follow-up step of a wake, ended by a failure the step itself does not classify. It opens a
    // scope, resolves the library out of it and reaches the host's own job enqueue, and a failure in
    // any of those leaves the batch pending for the next wake to try again.
    [LoggerMessage(
        EventId = 2113, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a follow-up scan could not be started; the next wake tries again")]
    internal static partial void FollowUpFaulted(ILogger logger, Exception failure);

    // The interval read of a wake, which is a live query. The wake works to the declared default
    // instead, so the backstop keeps running at a slower cadence than a user configured - a state
    // nothing else about the extension would show.
    [LoggerMessage(
        EventId = 2114, Level = LogLevel.Warning,
        Message = "[WhisparrSync] the stored backstop interval could not be read; this wake worked to the default")]
    internal static partial void BackstopIntervalUnreadable(ILogger logger, Exception failure);

    // A pending follow-up batch let go at shutdown. The files are on disk and Cove's own library scan
    // finds them, so the drop is the correct outcome - but imports left uncovered with no trace are
    // not something a user could ever explain. The count and nothing else: the paths are per file.
    [LoggerMessage(
        EventId = 2109, Level = LogLevel.Information,
        Message = "[WhisparrSync] a follow-up scan over {Count} imported files was dropped at shutdown")]
    internal static partial void FollowUpBatchDropped(ILogger logger, int count);

    // A batch with nowhere to go, because the host's scan service could not be obtained from this
    // extension's container. Nothing could have been imported in that state either, so this reports a
    // reading that is not expected to occur rather than a failure to recover from.
    [LoggerMessage(
        EventId = 2110, Level = LogLevel.Warning,
        Message = "[WhisparrSync] no follow-up scan could be started over {Count} imported files")]
    internal static partial void FollowUpScanUnavailable(ILogger logger, int count);

    // The one best-effort catch on the enrichment call. The import succeeded and the item carries its
    // identity either way, so the failure is contained - but a scene left bare with no trace is not
    // something a user could ever explain. The registrable domain of the source and nothing else: it
    // names which source did not answer and can carry neither a key nor a path.
    [LoggerMessage(
        EventId = 2106, Level = LogLevel.Information,
        Message = "[WhisparrSync] the metadata source at {Source} applied nothing to a newly imported scene")]
    internal static partial void EnrichmentContained(ILogger logger, string source, Exception failure);

    // The host's own import declining a file this product verified. Contained rather than
    // propagated: it is raised into a route whose declared results hold no failure, and into a walk
    // that has to keep reading to reach its mark. The exception and nothing else — the path is a
    // caller-supplied string and a log sink is durable and readable.
    [LoggerMessage(
        EventId = 2111, Level = LogLevel.Warning,
        Message = "[WhisparrSync] the host's own import would not take a verified file")]
    internal static partial void HostImportContained(ILogger logger, Exception failure);

    // One record of a walk the ingest could not take. The walk goes on, because the mark says how far
    // history has been read and a record that could not be taken is not a page that was not read -
    // but a channel quietly taking nothing is not something a user could ever explain. The generation
    // and the exception: no path, no address.
    [LoggerMessage(
        EventId = 2112, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a backstop record from {Generation} could not be ingested; the walk went on")]
    internal static partial void BackstopRecordContained(
        ILogger logger,
        WhisparrGeneration generation,
        Exception failure);
}
