namespace WhisparrSync.Import;

/// <summary>What one ingest did, named by cause.</summary>
/// <remarks>
/// Every value that is not <see cref="Imported"/> names a distinct reason nothing was registered, so
/// a refusal a user has to act on is never reported as the same thing as one they cannot. This is
/// the extension's own reading, not the answer the inbound route gives: an anonymous caller is told
/// only whether the event was one this product acts on.
/// </remarks>
public enum ImportOutcome
{
    /// <summary>Exactly one candidate verified and the host registered it.</summary>
    Imported,

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
}

/// <summary>
/// The one home both ingest channels enter, and the only place a file becomes a library item.
/// </summary>
/// <remarks>
/// Each channel projects its own input into <see cref="ImportCandidate"/> and hands it here, so the
/// classification, the refusals and the bookkeeping have one writer and cannot drift between the
/// live channel and the periodic one.
/// </remarks>
internal interface IImportCore
{
    /// <summary>Ingests <paramref name="candidate"/>, and reports what that did.</summary>
    Task<ImportOutcome> IngestAsync(ImportCandidate candidate, CancellationToken ct);
}
