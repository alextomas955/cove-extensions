using WhisparrSync.Contracts;

namespace WhisparrSync.Import;

// One file an instance says it has, whichever channel reported it. Both feeders project into this
// type, so the ingest core has one input.
// No reported root: neither generation's import event carries one, so the roots are read from the
// instance instead.
// The reported path is the file as the reporting system spells it, and is never passed to the
// host. The two systems need not spell it the same way.
// A candidate at the right path with the wrong reported size is a different file. The remote id is
// the only signal a scene is matched on, and is null when the delivery carried none.
internal sealed record ImportCandidate(
    WhisparrGeneration Generation,
    string EventType,
    string ReportedPath,
    long? ReportedSize,
    string? RemoteId);
