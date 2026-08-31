using Microsoft.Extensions.Logging;

namespace WhisparrSync.Import;

/// <summary>
/// The follow-up scan every import is covered by, collected into batches rather than started per file.
/// </summary>
/// <remarks>
/// The host's job enqueue deduplicates nothing and defaults to exclusive, so one scan per imported
/// file turns a burst of grabs into that many serialised library scans.
/// <para>
/// The pending batch is held here and nowhere else. A per-file collection persisted to extension
/// storage is the shape the scale rules forbid, and a pending batch is per file by definition.
/// </para>
/// <para>
/// A path is taken by exactly one batch: the take and the note run under one lock, so a path noted
/// while a batch is being started belongs to the next one rather than to neither.
/// </para>
/// </remarks>
internal sealed class FollowUpScanCoalescer(TimeProvider clock, ILogger log)
{
    /// <summary>How long a batch waits for a further import before it is started.</summary>
    /// <remarks>
    /// A constant and not a setting: it exists to bound the job count, and nothing about it is
    /// verifiable from outside the way the backstop interval is.
    /// </remarks>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(5);

    /// <summary>How many paths one pending batch holds before it is started regardless.</summary>
    /// <remarks>
    /// The batch is per file, so it is bounded here rather than left to the quiet period. A burst
    /// arriving faster than the quiet period restarts that period on every import, so without this
    /// the batch would grow with the burst and start nothing; the ceiling bounds both what is held
    /// and how long a path waits.
    /// </remarks>
    public const int PendingCeiling = 100;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private DateTimeOffset _notedAt;

    /// <summary>Whether a batch is pending and has been quiet long enough to start.</summary>
    /// <remarks>
    /// Read by a caller that would have to open a scope to start one, so a worker with nothing to
    /// cover resolves no host service.
    /// </remarks>
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

    /// <summary>Notes one path an import registered into the pending batch.</summary>
    /// <remarks>
    /// The batch is started here only when it has reached its ceiling. Every other batch is started
    /// by a flush.
    /// </remarks>
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

    /// <summary>Starts one scan over everything pending, whatever the quiet period says.</summary>
    /// <remarks>A pass boundary is a batch boundary, however recently that pass imported.</remarks>
    public void Flush(ICoveLibraryPort library) => Start(Take(), library);

    /// <summary>Starts one scan over everything pending, once the quiet period has elapsed.</summary>
    public void FlushIfQuiet(ICoveLibraryPort library)
    {
        string[] batch;
        lock (_gate)
        {
            batch = HasFallenQuiet() ? TakeUnderTheLock() : [];
        }

        Start(batch, library);
    }

    /// <summary>Drops a pending batch rather than flushing it after shutdown has begun.</summary>
    /// <remarks>
    /// A scan started here would reach a host that is stopping. The files are on disk and Cove's own
    /// library scan finds them, so the drop is recoverable where the start is not.
    /// </remarks>
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
