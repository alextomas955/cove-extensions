using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace WhisparrSync.Ingest;

/// <summary>
/// Turns a resolved (kind, path) into a Cove entity by calling the host's in-process
/// <see cref="IScanService"/> ingest — the IMPT-01 core. Resolves the scoped <see cref="IScanService"/>
/// from a fresh <c>CreateAsyncScope()</c> per call (the verified Phase-2 scope seam), never a long-lived
/// captured service. Import-in-place only: it never moves or deletes the imported file (SEC-03).
/// </summary>
internal sealed class IngestCoordinator(IServiceScopeFactory scopeFactory)
{
    /// <summary>
    /// Ingests <paramref name="path"/> as <paramref name="kind"/>, returning the created/updated Cove
    /// entity id. <paramref name="existingId"/> is null for a fresh import (create) and the existing Cove
    /// id for an upgrade-in-place.
    /// </summary>
    public async Task<int> IngestAsync(IngestKind kind, string path, int? existingId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var scan = scope.ServiceProvider.GetRequiredService<IScanService>();
        return kind switch
        {
            IngestKind.Video => await scan.ImportDownloadedVideoAsync(path, existingId, ct),
            IngestKind.Image => await scan.ImportDownloadedImageAsync(path, existingId, ct),
            IngestKind.Gallery => await scan.ImportDownloadedGalleryAsync(path, existingId, ct),
            IngestKind.Audio => await scan.ImportDownloadedAudioAsync(path, existingId, ct),
            IngestKind.Text => await scan.ImportDownloadedTextAsync(path, existingId, ct),
            _ => throw new NotSupportedException($"Unhandled ingest kind {kind}"),
        };
    }
}
