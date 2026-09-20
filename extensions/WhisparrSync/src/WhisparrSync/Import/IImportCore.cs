namespace WhisparrSync.Import;

/// <summary>What one ingest did, named by cause.</summary>
/// <remarks>
/// This is the extension's own reading, not the answer the inbound route gives: an anonymous caller
/// is told only whether the event was one this product acts on.
/// </remarks>
public enum ImportOutcome
{
    /// <summary>Exactly one candidate verified and the host registered it.</summary>
    Imported,

    /// <summary>
    /// The library already held a file at the resolved path, so this delivery registered nothing.
    /// </summary>
    /// <remarks>
    /// Not a refusal, and counted as an import by nothing. It is what the second of the two
    /// channels to reach one file reports.
    /// </remarks>
    AlreadyHeld,

    /// <summary>The delivery named an event this product does not act on.</summary>
    IgnoredEventType,

    /// <summary>The delivery named an event this product acts on and no path it could read.</summary>
    RefusedUnreadablePayload,

    /// <summary>The reporting instance could not be asked which roots it declares.</summary>
    RefusedNoReportedRoots,

    /// <summary>The reported path lies under none of the roots the reporting instance declares.</summary>
    RefusedPathOutsideEveryReportedRoot,

    /// <summary>The host declares no library path to look under.</summary>
    RefusedNoLibraryRoots,

    /// <summary>No candidate was a file on disk, so the product does not know where the file is.</summary>
    RefusedNotFound,

    /// <summary>
    /// More than one candidate was a file on disk, so the product cannot say which one the delivery
    /// meant, and refuses rather than choosing.
    /// </summary>
    RefusedAmbiguous,

    /// <summary>The host's own import could not be reached from this extension's container.</summary>
    RefusedHostImportUnavailable,

    /// <summary>
    /// More than one item in the library carries the identifier the delivery named, so the product
    /// cannot say which scene the delivery meant, and refuses rather than choosing.
    /// </summary>
    RefusedAmbiguousIdentity,

    /// <summary>
    /// The library holds a file row at the resolved path that no item claims, and the delivery named
    /// no identifier to say which item should claim it.
    /// </summary>
    /// <remarks>
    /// Counted against no Whisparr root: there is nothing for the user to correct at a root they
    /// configured, so the log line is the whole report.
    /// </remarks>
    RefusedDetachedFileWithoutIdentity,

    /// <summary>The host was asked to take a verified file and would not.</summary>
    /// <remarks>
    /// Counted against the reporting root, unlike an import service that could not be obtained: the
    /// path the host declined came from that root.
    /// </remarks>
    RefusedHostRefusedFile,
}

// Both ingest channels enter here, and this is the only place a file becomes a library item, so the
// classification, the refusals and the bookkeeping have one writer.
internal interface IImportCore
{
    Task<ImportOutcome> IngestAsync(ImportCandidate candidate, CancellationToken ct);
}
