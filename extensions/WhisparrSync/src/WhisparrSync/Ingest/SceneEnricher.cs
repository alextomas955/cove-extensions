using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace WhisparrSync.Ingest;

/// <summary>
/// The remote id a Whisparr On-Import carries for the scene it just imported (StashDB on v3, ThePornDB on
/// v2), plus the endpoint it belongs to — the same endpoint+id a Cove scene carries after Identify.
/// </summary>
internal readonly record struct SceneIdentity(string RemoteId, string Endpoint);

/// <summary>
/// Enriches a freshly-ingested Cove video with the identity Whisparr already knew — exactly once: stamps
/// the remote id (the durable link), best-effort identifies the video by that id through the host
/// <see cref="IMetadataServerService"/> (title, date, studio, performers, tags, cover — creating the studio /
/// performers when missing), and hands the file to a scan/generate pass. Applies no filesystem mutation —
/// the ingest stays in place and loop-safe; only Cove-side metadata + generated assets change.
/// </summary>
internal static class SceneEnricher
{
    public static async Task EnrichAsync(
        DbContext db, IMetadataServerService? metadata, IScanService scan,
        int coveId, SceneIdentity identity, string path, CancellationToken ct)
    {
        var video = await db.Set<Video>()
            .Include(v => v.RemoteIds)
            .FirstOrDefaultAsync(v => v.Id == coveId, ct);
        if (video is null)
        {
            return;
        }

        // Cove and Whisparr key a scene on the SAME StashDB (v3) / ThePornDB (v2) id, so an existing remote-id
        // stamp for this endpoint means the scene was ALREADY identified — by an earlier ingest, or by the
        // user via Cove's own Identify. Enrichment then does nothing: re-running would re-fetch the metadata
        // server and re-apply (clobbering any manual metadata edits — the merge overwrites scalar fields) and
        // re-generate assets that already exist, for no gain. So an already-linked scene is a clean no-op and
        // enrichment happens exactly once per scene, whatever channel (webhook / reconcile / upgrade) re-sees it.
        var alreadyIdentified = video.RemoteIds.Any(r =>
            string.Equals(r.Endpoint, identity.Endpoint, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.RemoteId, identity.RemoteId, StringComparison.OrdinalIgnoreCase));
        if (alreadyIdentified)
        {
            return;
        }

        // The durable link Cove's own Identify and the extension's status views key on.
        video.RemoteIds.Add(new VideoRemoteId { VideoId = coveId, Endpoint = identity.Endpoint, RemoteId = identity.RemoteId });
        await db.SaveChangesAsync(ct);

        // MergeVideoAsync throws when no metadata-server box is configured for the endpoint and does network
        // I/O; the file is already imported and the id already stamped, so any failure degrades to
        // "linked but not yet scraped" rather than a broken ingest. A null importConfig takes Cove's defaults
        // (set cover/tags/performers/studio, creating missing studio+performers) — a full identify by id.
        if (metadata is not null)
        {
            try
            {
                if (await metadata.MergeVideoAsync(video, identity.Endpoint, identity.RemoteId, importConfig: null, ct))
                {
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception)
            {
                // best-effort identify; the guaranteed outcome is the stamped id above
            }
        }

        // Cove's own scan/generate for the imported file (covers, previews, sprites, phash, mediainfo).
        // StartScan returns a job id and does not block, so the webhook response is not held on generation.
        scan.StartScan(new ScanOperationOptions
        {
            Paths = [path],
            Rescan = true,
            GenerateCovers = true,
            GeneratePreviews = true,
            GenerateSprites = true,
            GeneratePhashes = true,
        });
    }
}
