using Microsoft.Extensions.Logging;

namespace WhisparrSync.Import;

// Imports are collected into batches because the host's job enqueue deduplicates nothing and
// defaults to exclusive, so one scan per imported file turns a burst of grabs into that many
// serialised library scans.
// The pending batch is held in memory only. A per-file collection must never be persisted to
// extension storage.
// A path is taken by exactly one batch: the take and the note run under one lock, so a path noted
// while a batch starts belongs to the next batch.
internal sealed class FollowUpScanCoalescer(TimeProvider clock, ILogger log)
{
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(5);

    // A burst arriving faster than the quiet period restarts that period on every import, so
    // without this ceiling the batch would grow with the burst and start nothing.
    public const int PendingCeiling = 100;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private DateTimeOffset _notedAt;

    // Read before opening a scope, so a worker with nothing to cover resolves no host service.
    public bool ScanIsDue
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count > 0 && HasFallenQuiet();
            }
        }
    }

    // Starts the batch here only at the ceiling. Every other batch is started by a flush.
    public void NoteImported(string path, ICoveLibraryPort library)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string[] full;
        lock (_gate)
        {
            _pending.Add(path);
            _notedAt = clock.GetUtcNow();
            if (_pending.Count < PendingCeiling)
            {
                return;
            }

            full = TakeUnderTheLock();
        }

        Start(full, library);
    }

    // Starts one scan over everything pending, whatever the quiet period says: a pass boundary is a
    // batch boundary.
    public void Flush(ICoveLibraryPort library) => Start(Take(), library);

    public void FlushIfQuiet(ICoveLibraryPort library)
    {
        string[] batch;
        lock (_gate)
        {
            batch = HasFallenQuiet() ? TakeUnderTheLock() : [];
        }

        Start(batch, library);
    }

    // Drops the pending batch during shutdown rather than starting a scan against a stopping host.
    // The files are on disk and Cove's own library scan finds them, so the drop is recoverable.
    public void Drop()
    {
        var dropped = Take();
        if (dropped.Length > 0)
        {
            WhisparrSyncLog.FollowUpBatchDropped(log, dropped.Length);
        }
    }

    private bool HasFallenQuiet() => clock.GetUtcNow() - _notedAt >= QuietPeriod;

    private string[] Take()
    {
        lock (_gate)
        {
            return TakeUnderTheLock();
        }
    }

    private string[] TakeUnderTheLock()
    {
        if (_pending.Count == 0)
        {
            return [];
        }

        var batch = _pending.ToArray();
        _pending.Clear();
        return batch;
    }

    private void Start(string[] batch, ICoveLibraryPort library)
    {
        ArgumentNullException.ThrowIfNull(library);

        if (batch.Length == 0)
        {
            return;
        }

        if (!library.StartFollowUpScan(batch))
        {
            WhisparrSyncLog.FollowUpScanUnavailable(log, batch.Length);
        }
    }
}
