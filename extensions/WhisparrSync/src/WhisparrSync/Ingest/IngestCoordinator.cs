using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
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
/// kind label (null when unresolved — always <c>"Video"</c> otherwise, the only kind Whisparr ever hands back),
/// the created/updated Cove entity id (null unless imported), and a human-readable reason for a flagged
/// outcome (null when imported).
/// </summary>
internal readonly record struct IngestOutcome(IngestResult Result, string? Kind, int? CoveEntityId, string? Reason);

/// <summary>
/// Turns an imported path into a Cove entity via the host's in-process <see cref="IScanService"/> — the
/// core, with the path-containment guard and the never-silent fallback. Every ingest is
/// gated by <see cref="WhisparrRootGuard"/> FIRST (fail-closed); a resolved-and-contained path is imported
/// in place (never moved/deleted); a failed or kind-unresolvable in-root ingest falls back to a
/// scoped <c>StartScan</c> rather than throwing or failing silently.
/// </summary>
internal sealed class IngestCoordinator(
    IServiceScopeFactory scopeFactory,
    Func<CancellationToken, ValueTask<IReadOnlyList<string>>> allowedRootsProvider)
{
    private const string OutsideRootReason = "path outside known Whisparr root";

    /// <summary>
    /// The stable, machine-detectable reason for the one failure that means "sync is broken": Whisparr reported
    /// an imported file at a path Cove cannot open. It is almost always a path mismatch — Whisparr and Cove
    /// mount the same library at different paths. The settings surface filters the import log on this exact
    /// string to raise a visible warning, so it MUST stay constant.
    /// </summary>
    internal const string PathNotVisibleReason = "Cove can't open this path — Whisparr and Cove must see the library at the same path";

    /// <summary>
    /// Ingests <paramref name="path"/> (kind resolved from its extension), returning the classified outcome.
    /// <paramref name="existingId"/> is null for a fresh create and the existing Cove id for an upgrade-in-place.
    /// When <paramref name="identity"/> is non-null and the import succeeds, the new video is enriched via
    /// <see cref="SceneEnricher"/> (stamp the remote id, best-effort identify, scan/generate).
    /// </summary>
    public async Task<IngestOutcome> IngestAsync(string path, int? existingId, SceneIdentity? identity, CancellationToken ct)
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

        // A path we don't recognize as video (a non-default extension, until the user's configured list is
        // read) is never dropped silently — it routes to the scoped-scan fallback so Cove still discovers it.
        if (!FileKindResolver.IsVideo(path))
        {
            return Fallback(scan, path, kind: null, "unresolved file kind");
        }

        // A Whisparr On-Import carries no Cove principal, so the ambient one is Anonymous. CoveContext applies
        // per-principal authorization query filters, so an Anonymous read returns ZERO rows — the enrichment's
        // video lookup would silently find nothing and the scene would land un-stamped. This ingest is a
        // trusted, host-triggered library operation, so it runs as System (filters bypassed) for its whole span
        // — import, enrich, scan — and the prior principal is restored so the surrounding request is untouched.
        var principals = scope.ServiceProvider.GetService<ICurrentPrincipalAccessor>();
        var previousPrincipal = principals?.Current;
        principals?.Set(CovePrincipal.System());
        try
        {
            var coveId = await scan.ImportDownloadedVideoAsync(path, existingId, ct);
            if (identity is { } id)
            {
                // The DbContext is absent in a host with no DB scope (and in the coordinator's own unit tests),
                // so enrichment is skipped there — the import itself already succeeded.
                var db = scope.ServiceProvider.GetService<DbContext>();
                if (db is not null)
                {
                    var metadata = scope.ServiceProvider.GetService<IMetadataServerService>();
                    await SceneEnricher.EnrichAsync(db, metadata, scan, coveId, id, path, ct);
                }
            }

            return new IngestOutcome(IngestResult.Imported, "Video", coveId, null);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Cove could not open the path Whisparr reported — the sync-broken signal. It passed the root guard
            // (so it's a legitimate Whisparr path), yet Cove can't see the file: almost always a path mismatch.
            // Tagged distinctly so the settings surface can warn, then fall back to a scoped scan regardless.
            return Fallback(scan, path, "Video", PathNotVisibleReason);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException)
        {
            // Another IO/format failure (not a missing path) — classify-not-throw and fall back to a scoped scan.
            return Fallback(scan, path, "Video", $"ingest failed ({ex.GetType().Name})");
        }
        finally
        {
            principals?.Set(previousPrincipal);
        }
    }

    // Scoped-scan fallback: point a manual scan at the imported file's folder so Cove indexes it out-of-band.
    // Only ever reached for an in-root path (the guard ran first), so it never scans an attacker-chosen dir.
    private static IngestOutcome Fallback(IScanService scan, string path, string? kind, string reason)
    {
        var folder = Path.GetDirectoryName(path);
        scan.StartScan(new ScanOperationOptions { Paths = string.IsNullOrEmpty(folder) ? [] : [folder] });
        return new IngestOutcome(IngestResult.Flagged, kind, null, reason);
    }
}
