using WhisparrSync.Contracts;

namespace WhisparrSync.Import;

/// <summary>One file an instance says it has, whichever channel reported it.</summary>
/// <remarks>
/// Both feeders project into this one type, so the ingest core has a single input and the
/// classification, the refusals and the bookkeeping have one writer rather than one per channel.
/// <para>
/// There is no reported root here. Neither generation's import event carries one, measured on both
/// against the builds named in the committed payload fixtures, so the roots are read from the
/// instance itself rather than taken off a delivery.
/// </para>
/// </remarks>
/// <param name="Generation">The generation the delivery came from.</param>
/// <param name="EventType">The event type the delivery named, in the instance's own spelling.</param>
/// <param name="ReportedPath">
/// The absolute path the instance reported, in its own spelling. Never passed to the host: it names
/// the file as the REPORTING system sees it, and the two systems need not spell it the same way.
/// </param>
/// <param name="ReportedSize">
/// The size the delivery reported, or null when it carried none. A candidate that exists at the
/// right path and the wrong size is a different file.
/// </param>
/// <param name="RemoteId">
/// The shared remote identifier the delivery carried, or null when it carried none. The only signal
/// a scene may ever be matched on.
/// </param>
internal sealed record ImportCandidate(
    WhisparrGeneration Generation,
    string EventType,
    string ReportedPath,
    long? ReportedSize,
    string? RemoteId);
