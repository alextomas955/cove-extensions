using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace WhisparrSync.Ingest;

/// <summary>The classified result of an ingest attempt — the UI vocabulary is Imported / Skipped / Flagged (Skipped is decided upstream by the ledger).</summary>
internal enum IngestResult
{
    Imported,
    Flagged,
}

/// <summary>
/// The outcome of one <see cref="IngestCoordinator.IngestAsync"/> call: the classification, the resolved
/// kind (null when unresolved), the created/updated Cove entity id (null unless imported), and a
/// human-readable reason for a flagged outcome (null when imported).
/// </summary>
internal readonly record struct IngestOutcome(IngestResult Result, IngestKind? Kind, int? CoveEntityId, string? Reason);

/// <summary>
/// Turns an imported path into a Cove entity via the host's in-process <see cref="IScanService"/> — the
/// IMPT-01 core, with the T-03-PT containment guard and the IMPT-05 never-silent fallback. Every ingest is
/// gated by <see cref="WhisparrRootGuard"/> FIRST (fail-closed); a resolved-and-contained path is imported
/// in place (SEC-03 — never moved/deleted); a failed or kind-unresolvable in-root ingest falls back to a
/// scoped <c>StartScan</c> rather than throwing or failing silently.
/// </summary>
internal sealed class IngestCoordinator(
    IServiceScopeFactory scopeFactory,
    Func<CancellationToken, ValueTask<IReadOnlyList<string>>> allowedRootsProvider)
{
    private const string OutsideRootReason = "path outside known Whisparr root";

    /// <summary>
    /// Ingests <paramref name="path"/> (kind resolved from its extension), returning the classified outcome.
    /// <paramref name="existingId"/> is null for a fresh create and the existing Cove id for an upgrade-in-place.
    /// </summary>
    public async Task<IngestOutcome> IngestAsync(string path, int? existingId, CancellationToken ct)
    {
        // FAIL-CLOSED containment guard BEFORE any ImportDownloaded*/StartScan: an out-of-root path (or an
        // unavailable/empty root set) is rejected with no ingest and no scan — an attacker-chosen path never
        // reaches the host ingest, and the reject is audited by the caller.
        var roots = await allowedRootsProvider(ct);
        if (!WhisparrRootGuard.IsWithinAnyRoot(path, roots))
        {
            return new IngestOutcome(IngestResult.Flagged, null, null, OutsideRootReason);
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var scan = scope.ServiceProvider.GetRequiredService<IScanService>();

        // A kind we cannot resolve (a non-default extension until 03-03 reads the user's lists) is never
        // dropped silently — it routes to the scoped-scan fallback so Cove still discovers the file (IMPT-05).
        if (!FileKindResolver.TryResolve(path, out var kind))
        {
            return Fallback(scan, path, kind: null, "unresolved file kind");
        }

        try
        {
            var coveId = await ImportAsync(scan, kind, path, existingId, ct);
            return new IngestOutcome(IngestResult.Imported, kind, coveId, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or NotSupportedException or IOException)
        {
            // The path was gone / not yet visible / unsupported by ImportDownloaded* — classify-not-throw
            // (WhisparrClient.SendAsync discipline) and fall back to a scoped scan instead of a silent failure.
            return Fallback(scan, path, kind, $"ingest failed ({ex.GetType().Name})");
        }
    }

    private static Task<int> ImportAsync(IScanService scan, IngestKind kind, string path, int? existingId, CancellationToken ct)
        => kind switch
        {
            IngestKind.Video => scan.ImportDownloadedVideoAsync(path, existingId, ct),
            IngestKind.Image => scan.ImportDownloadedImageAsync(path, existingId, ct),
            IngestKind.Gallery => scan.ImportDownloadedGalleryAsync(path, existingId, ct),
            IngestKind.Audio => scan.ImportDownloadedAudioAsync(path, existingId, ct),
            IngestKind.Text => scan.ImportDownloadedTextAsync(path, existingId, ct),
            _ => throw new NotSupportedException($"Unhandled ingest kind {kind}"),
        };

    // Scoped-scan fallback: point a manual scan at the imported file's folder so Cove indexes it out-of-band.
    // Only ever reached for an in-root path (the guard ran first), so it never scans an attacker-chosen dir.
    private static IngestOutcome Fallback(IScanService scan, string path, IngestKind? kind, string reason)
    {
        var folder = Path.GetDirectoryName(path);
        scan.StartScan(new ScanOperationOptions { Paths = string.IsNullOrEmpty(folder) ? [] : [folder] });
        return new IngestOutcome(IngestResult.Flagged, kind, null, reason);
    }
}
