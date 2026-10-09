namespace WhisparrSync.Import;

internal enum BackstopPassOutcome
{
    NotConfigured,

    // No mark was stored, so the pass recorded where history ends and imported nothing.
    FirstConnect,

    Walked,

    RefusedUnreadableAnswer,

    // A page's records were not in an order this product understands, so it imported from none.
    RefusedPageOrder,

    RefusedUnreachable,
}

// A fixed member set: counts and instants, never rows, so the answer's size does not grow with how
// much history the walk read. Contained counts records the ingest could not take at all, apart from
// WithoutCandidate, which counts records that named no readable path.
internal sealed record BackstopPassResult(
    BackstopPassOutcome Outcome,
    DateTimeOffset? Watermark,
    int PagesRead,
    int RecordsTaken,
    int Imported,
    int WithoutCandidate,
    int Contained);

// One walk back through a Whisparr instance's import history. It reads and only reads: no method it
// reaches can make the instance search for, download, move or delete anything.
internal interface IBackstopPass
{
    Task<BackstopPassResult> RunAsync(CancellationToken ct);
}
