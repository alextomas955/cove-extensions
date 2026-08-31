namespace WhisparrSync.Import;

/// <summary>How one backstop pass ended.</summary>
internal enum BackstopPassOutcome
{
    /// <summary>The selected generation has no address and key to read from.</summary>
    NotConfigured,

    /// <summary>No mark was stored, so the pass recorded where history ends and imported nothing.</summary>
    FirstConnect,

    /// <summary>The pass walked back to the stored mark.</summary>
    Walked,

    /// <summary>The instance answered with something this product could not read as a page.</summary>
    RefusedUnreadableAnswer,

    /// <summary>
    /// A page's records were not in an order this product understands, so it imported from none of
    /// them.
    /// </summary>
    RefusedPageOrder,

    /// <summary>The instance could not be reached.</summary>
    RefusedUnreachable,
}

/// <summary>What one backstop pass did.</summary>
/// <remarks>
/// A fixed member set. A pass that read a thousand records answers with the same shape as one that
/// read none: the walk may be linear in time, and what it hands back is not.
/// </remarks>
/// <param name="Outcome">How the pass ended.</param>
/// <param name="Watermark">Where the pass left the mark, or null when it wrote none.</param>
/// <param name="PagesRead">How many pages the walk asked for.</param>
/// <param name="RecordsTaken">How many records the walk read past the mark.</param>
/// <param name="Imported">How many of those the ingest core registered.</param>
/// <param name="WithoutCandidate">How many named an import this product could read no path from.</param>
internal sealed record BackstopPassResult(
    BackstopPassOutcome Outcome,
    DateTimeOffset? Watermark,
    int PagesRead,
    int RecordsTaken,
    int Imported,
    int WithoutCandidate);

/// <summary>One walk back through a Whisparr instance's import history.</summary>
/// <remarks>
/// The channel with nobody watching. It reads and only reads: no method it reaches can express a
/// request that makes the instance search for, download, move or delete anything.
/// </remarks>
internal interface IBackstopPass
{
    /// <summary>Runs one pass over the selected generation, and reports what it did.</summary>
    Task<BackstopPassResult> RunAsync(CancellationToken ct);
}
