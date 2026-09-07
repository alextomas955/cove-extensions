using System.Text.Json;
using Cove.Extensions.Shared;
using Cove.Plugins;
using WhisparrSync.Ingest;

namespace WhisparrSync.State;

/// <summary>
/// One unresolved path-mismatch import failure retained for the sync-health banner: Whisparr reported a file
/// at a path Cove could not open. <see cref="UtcTicks"/> is a server-written <c>DateTime.UtcNow.Ticks</c>
/// (never a browser value). <see cref="Path"/> is the offending Whisparr path the banner samples.
/// </summary>
internal readonly record struct PathMismatchFailure(long UtcTicks, string Path);

/// <summary>
/// The bounded sync-status record that replaces the old unbounded per-attempt journal. It persists
/// only the two aggregates the settings UI actually reads: the last webhook delivery tick (the "last event"
/// line) and the sync-health signal — pre-reduced instead of recomputed from a growing array.
/// </summary>
/// <remarks>
/// <see cref="LastWebhookEventTicks"/> is the newest webhook delivery of ANY outcome (the poll never stamps it,
/// matching the old <c>source=="webhook"</c> last-event derivation). <see cref="LastSuccessTicks"/> is the
/// newest successful import from EITHER channel. <see cref="UnresolvedPathMismatch"/> is the EXACT count of
/// path-mismatch failures strictly after the last success, kept precise so the banner's rendered count never
/// truncates; a success zeroes it (the <c>SyncHealthOf</c> "a later success clears the banner" rule, stored
/// pre-reduced). <see cref="RecentFailures"/> is the newest-N sample ring backing the banner's ≤3 sample paths;
/// only the sample set is capped, never the rendered count.
/// </remarks>
internal readonly record struct ImportStatus(
    long LastWebhookEventTicks,
    long LastSuccessTicks,
    int UnresolvedPathMismatch,
    PathMismatchFailure[] RecentFailures)
{
    // The sample ring cap: the banner shows ≤3 sample paths, so 16 leaves ample dedup headroom while bounding
    // the retained-plaintext-path footprint. The rendered COUNT is exact (UnresolvedPathMismatch), not this cap.
    internal const int MaxRecentFailures = 16;

    /// <summary>The healthy, never-yet-imported default: no event, no success, no unresolved failures.</summary>
    public static ImportStatus Empty => new(0L, 0L, 0, []);
}

/// <summary>
/// The auto-import sync-status store: a single bounded <see cref="ImportStatus"/> blob over one
/// <see cref="IExtensionStore"/> key. Rides <see cref="SingleWriterBlobStore{T}"/> for the process-wide
/// single-writer gate so a concurrent webhook + poll outcome cannot tear the blob. Bounded by construction —
/// the base <c>Compact</c> hook stays a no-op.
/// </summary>
internal sealed class ImportLog(IExtensionStore store) : SingleWriterBlobStore<ImportStatus>(store, Key)
{
    private const string Key = "importlog";

    private const string ImportedResult = "Imported";

    /// <summary>Loads the sync-status record; the healthy <see cref="ImportStatus.Empty"/> default on an absent key or an unreadable blob.</summary>
    public async Task<ImportStatus> LoadStatusAsync(CancellationToken ct = default)
        => Parse(await LoadBlobAsync(ct));

    /// <summary>
    /// Folds one auto-import outcome into the bounded status record (gated read-modify-write). A webhook
    /// delivery stamps the last-event tick (the poll does not). A successful import from either channel
    /// advances the success tick and clears the unresolved-failure window; a path-mismatch failure strictly
    /// after the last success increments the exact count and pushes onto the capped sample ring; any other
    /// outcome only stamps the event tick. This holds the <c>SyncHealthOf</c> banner semantics pre-reduced.
    /// </summary>
    public Task RecordOutcomeAsync(
        bool fromWebhook, long utcTicks, string result, string? reason, string path, CancellationToken ct = default)
        => RunExclusiveAsync(async () =>
        {
            var status = Parse(await LoadBlobAsync(ct));
            var lastEvent = fromWebhook ? Math.Max(status.LastWebhookEventTicks, utcTicks) : status.LastWebhookEventTicks;

            if (result == ImportedResult)
            {
                await PersistAsync(new ImportStatus(lastEvent, Math.Max(status.LastSuccessTicks, utcTicks), 0, []), ct);
                return;
            }

            // Only a path-mismatch STRICTLY after the last success is a sync-broken signal — a success ties or
            // supersedes a same-tick mismatch (the SyncHealthOf `> lastSuccess` boundary), and out-of-root or
            // other flags are not a path-mismatch and never trip the banner.
            var isMismatch = reason == IngestCoordinator.PathNotVisibleReason && utcTicks > status.LastSuccessTicks;
            var count = isMismatch ? status.UnresolvedPathMismatch + 1 : status.UnresolvedPathMismatch;
            var recent = isMismatch
                ? AppendCapped(status.RecentFailures, new PathMismatchFailure(utcTicks, path))
                : status.RecentFailures;

            await PersistAsync(
                status with { LastWebhookEventTicks = lastEvent, UnresolvedPathMismatch = count, RecentFailures = recent },
                ct);
        }, ct);

    // Keep the newest MaxRecentFailures sample entries (append-ordered), dropping the oldest past the cap.
    private static PathMismatchFailure[] AppendCapped(PathMismatchFailure[] current, PathMismatchFailure entry)
    {
        if (current.Length < ImportStatus.MaxRecentFailures)
        {
            return [.. current, entry];
        }

        var next = new PathMismatchFailure[ImportStatus.MaxRecentFailures];
        Array.Copy(current, current.Length - ImportStatus.MaxRecentFailures + 1, next, 0, ImportStatus.MaxRecentFailures - 1);
        next[^1] = entry;
        return next;
    }

    // Defensive single-object parse: an absent/corrupt blob — AND the pre-reduction ImportLogEntry[] ARRAY blob
    // (a JSON array cannot deserialize into this object) — loads as the empty status, never throws. An old
    // array blob thus becomes a fresh empty record that reseeds on the next webhook; only never-rendered history
    // is lost.
    private static ImportStatus Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return ImportStatus.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize(json, IngestJsonContext.Default.ImportStatus);
        }
        catch (JsonException)
        {
            return ImportStatus.Empty;
        }
    }

    private Task PersistAsync(ImportStatus status, CancellationToken ct)
        => StoreBlobAsync(JsonSerializer.Serialize(status, IngestJsonContext.Default.ImportStatus), ct);
}
