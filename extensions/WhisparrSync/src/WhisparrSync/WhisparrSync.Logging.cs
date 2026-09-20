using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    [LoggerMessage(
        EventId = 2000, Level = LogLevel.Warning,
        Message = "[WhisparrSync] the host supplied no Cove configuration; nothing measured from it can be resolved")]
    private partial void LogNoCoveConfiguration();

    // Information rather than Warning: the extension loads either way and reports the reading in its
    // own probe response.
    [LoggerMessage(
        EventId = 2001, Level = LogLevel.Information,
        Message = "[WhisparrSync] IScanService could not be obtained from this extension's container")]
    private partial void LogNoScanService();

    [LoggerMessage(
        EventId = 2002, Level = LogLevel.Information,
        Message = "[WhisparrSync] IMetadataServerService could not be obtained from this extension's container")]
    private partial void LogNoMetadataServerService();

    [LoggerMessage(
        EventId = 2003, Level = LogLevel.Warning,
        Message = "[WhisparrSync] the stored generation could not be read at load; every UI surface is registered until a settings save establishes one")]
    private partial void LogNoStoredGeneration();
}

// No template here takes an API key or an address: an address may carry credentials in its
// user-info, and a log sink is durable and readable. A host name carries none and is enough to
// diagnose an outbound failure.
internal static partial class WhisparrSyncLog
{
    [LoggerMessage(
        EventId = 2100, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a connection test to {Host} produced no response ({Failure})")]
    internal static partial void ConnectionTransportFailure(
        ILogger logger,
        ConnectionTransportFailure failure,
        string host);

    [LoggerMessage(
        EventId = 2101, Level = LogLevel.Information,
        Message = "[WhisparrSync] a concurrent mint had already stored the callback secret; the stored one is in use")]
    internal static partial void ConcurrentMintLostToAnExistingRow(ILogger logger);

    [LoggerMessage(
        EventId = 2102, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a callback registration on {Generation} answered {WriteStatus} and the notification read back differently")]
    internal static partial void CallbackRegistrationDidNotTake(
        ILogger logger,
        WhisparrGeneration generation,
        int writeStatus);

    // The root, not the refused path: the path is caller-supplied, and the root comes from the
    // configured instance's own answer.
    [LoggerMessage(
        EventId = 2103, Level = LogLevel.Information,
        Message = "[WhisparrSync] an import from {Generation} under {Root} registered nothing ({Outcome})")]
    internal static partial void ImportRefused(
        ILogger logger,
        WhisparrGeneration generation,
        ImportOutcome outcome,
        string root);

    [LoggerMessage(
        EventId = 2104, Level = LogLevel.Information,
        Message = "[WhisparrSync] {Generation} sent the event type {EventType}, which this product does not act on")]
    internal static partial void ImportEventTypeIgnored(
        ILogger logger,
        WhisparrGeneration generation,
        string eventType);

    [LoggerMessage(
        EventId = 2105, Level = LogLevel.Warning,
        Message = "[WhisparrSync] reading {Generation}'s declared root folders from {Host} produced no response")]
    internal static partial void ReportedRootReadFailed(
        ILogger logger,
        WhisparrGeneration generation,
        string host);

    [LoggerMessage(
        EventId = 2131, Level = LogLevel.Warning,
        Message = "[WhisparrSync] asking {Generation} at {Host} what it holds at a candidate path produced no response")]
    internal static partial void FolderProbeFailed(
        ILogger logger,
        WhisparrGeneration generation,
        string host);

    [LoggerMessage(
        EventId = 2107, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a backstop pass over {Generation} at {Host} imported nothing and kept its place ({Outcome})")]
    internal static partial void BackstopPassRefused(
        ILogger logger,
        WhisparrGeneration generation,
        BackstopPassOutcome outcome,
        string host);

    [LoggerMessage(
        EventId = 2108, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a backstop pass ended in an unexpected failure; the next wake tries again")]
    internal static partial void BackstopPassFaulted(ILogger logger, Exception failure);

    [LoggerMessage(
        EventId = 2113, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a follow-up scan could not be started; the next wake tries again")]
    internal static partial void FollowUpFaulted(ILogger logger, Exception failure);

    [LoggerMessage(
        EventId = 2114, Level = LogLevel.Warning,
        Message = "[WhisparrSync] the stored backstop interval could not be read; this wake worked to the default")]
    internal static partial void BackstopIntervalUnreadable(ILogger logger, Exception failure);

    // The count only: a path list grows with the batch.
    [LoggerMessage(
        EventId = 2109, Level = LogLevel.Information,
        Message = "[WhisparrSync] a follow-up scan over {Count} imported files was dropped at shutdown")]
    internal static partial void FollowUpBatchDropped(ILogger logger, int count);

    [LoggerMessage(
        EventId = 2110, Level = LogLevel.Warning,
        Message = "[WhisparrSync] no follow-up scan could be started over {Count} imported files")]
    internal static partial void FollowUpScanUnavailable(ILogger logger, int count);

    // The source's registrable domain, never its address: a domain carries no credential.
    [LoggerMessage(
        EventId = 2106, Level = LogLevel.Information,
        Message = "[WhisparrSync] the metadata source at {Source} applied nothing to a newly imported scene ({Failure})")]
    internal static partial void EnrichmentContained(ILogger logger, string source, string failure);

    [LoggerMessage(
        EventId = 2115, Level = LogLevel.Warning,
        Message = "[WhisparrSync] the record the metadata source at {Source} supplied for a newly imported scene could not be written")]
    internal static partial void EnrichmentNotCommitted(ILogger logger, string source, Exception failure);

    // The failure's classification only: the path is caller-supplied and the failure's own message
    // quotes it.
    [LoggerMessage(
        EventId = 2111, Level = LogLevel.Warning,
        Message = "[WhisparrSync] the host's own import would not take a verified file ({Failure})")]
    internal static partial void HostImportContained(ILogger logger, string failure);

    [LoggerMessage(
        EventId = 2112, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a backstop record from {Generation} could not be ingested ({Failure}); the walk went on")]
    internal static partial void BackstopRecordContained(
        ILogger logger,
        WhisparrGeneration generation,
        string failure);

    [LoggerMessage(
        EventId = 2117, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a monitoring request to {Generation} at {Host} produced no response ({Failure})")]
    internal static partial void MonitoringRequestContained(
        ILogger logger,
        WhisparrGeneration generation,
        string failure,
        string host);

    // Never the body: the value that passed the bound is the one that must not travel.
    [LoggerMessage(
        EventId = 2120, Level = LogLevel.Warning,
        Message = "[WhisparrSync] an answer from {Host} was larger than the {Bound} bytes this extension reads at once and was refused")]
    internal static partial void ResponseBeyondReadBound(ILogger logger, string host, long bound);

    // The audit trail for the one verb that makes an instance download. The entity kind only: which
    // entity, which instance and which key are either caller-supplied or credentials.
    [LoggerMessage(
        EventId = 2119, Level = LogLevel.Information,
        Message = "[WhisparrSync] a search was issued for a {Kind} the connected instance monitors")]
    internal static partial void SearchIssued(ILogger logger, WhisparrEntityKind kind);

    // The per-scene half of the search above, given no arguments: which scene a reader asked for is
    // theirs.
    [LoggerMessage(
        EventId = 2125, Level = LogLevel.Information,
        Message = "[WhisparrSync] a search was issued for one scene the connected instance holds")]
    internal static partial void SceneSearchIssued(ILogger logger);

    [LoggerMessage(
        EventId = 2126, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a library count could not be finished ({Failure}); no count was held")]
    internal static partial void SyncCountDidNotFinish(ILogger logger, string failure);

    // Once per count with how many it covered, not one line per studio.
    [LoggerMessage(
        EventId = 2129, Level = LogLevel.Information,
        Message = "[WhisparrSync] the metadata source names no site for {Studios} of the library's studios; they were counted as carrying no usable id")]
    internal static partial void StudiosTheSourceNamesNoSiteFor(ILogger logger, int studios);

    // One line per refused studio: the run's own count names none of them, and a reader needs the
    // two ids to look one up.
    [LoggerMessage(
        EventId = 2130, Level = LogLevel.Warning,
        Message = "[WhisparrSync] studio {StudioId}, carrying site identifier {RemoteId}, was not registered ({Reason})")]
    internal static partial void SiteRegistrationRefused(
        ILogger logger, int studioId, string remoteId, string reason);

    // Once per site, not once per scene.
    [LoggerMessage(
        EventId = 2127, Level = LogLevel.Warning,
        Message = "[WhisparrSync] scene numbers could not be read from the metadata provider ({Failure}); those scenes were not monitored and the run went on")]
    internal static partial void SceneNumbersUnreadable(ILogger logger, string failure);

    // Once per site, not once per scene.
    [LoggerMessage(
        EventId = 2128, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a site's own scene rows could not be read ({Failure}); those scenes were counted as unresolved and the run went on")]
    internal static partial void SiteSceneRowsUnreadable(ILogger logger, string failure);

    [LoggerMessage(
        EventId = 2116, Level = LogLevel.Warning,
        Message = "[WhisparrSync] the stored options blob could not be read, so a change was NOT written over it; the stored configuration stands and the change was lost")]
    internal static partial void OptionsMutationRefusedOverUnreadableBlob(ILogger logger);

    // Never the body: a catalogue body carries titles and identifiers from someone's library scope.
    [LoggerMessage(
        EventId = 2121, Level = LogLevel.Warning,
        Message = "[WhisparrSync] an answer from {Provider} was larger than the {Bound} bytes this extension reads at once and was refused")]
    internal static partial void ProviderAnswerBeyondReadBound(
        ILogger logger, string provider, long bound);

    // The provider reports an unusable credential as a refusal inside a success status. Without this
    // line an expired key reads as an empty catalogue.
    [LoggerMessage(
        EventId = 2122, Level = LogLevel.Warning,
        Message = "[WhisparrSync] {Provider} refused the catalogue query; no catalogue was read")]
    internal static partial void ProviderRefusedTheQuery(ILogger logger, string provider);

    [LoggerMessage(
        EventId = 2123, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a catalogue read was contained ({Failure}); the page states it could not be read")]
    internal static partial void CatalogueReadContained(ILogger logger, string failure);

    [LoggerMessage(
        EventId = 2124, Level = LogLevel.Warning,
        Message = "[WhisparrSync] a scene status read was contained ({Failure}); the page states no status was read")]
    internal static partial void SceneStatusReadContained(ILogger logger, string failure);

    // Type names rather than Exception.Message, which can quote a filesystem path or a configured
    // address. A type name is chosen by whoever wrote the throw, so no part of it comes from a
    // caller or from a remote instance.
    internal static string Classify(Exception failure)
        => failure.InnerException is { } cause
            ? $"{failure.GetType().Name} caused by {cause.GetType().Name}"
            : failure.GetType().Name;
}
