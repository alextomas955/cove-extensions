using System.Runtime.CompilerServices;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Identity;
using WhisparrSync.Options;

namespace WhisparrSync.Library;

// Binds the base DbContext: this extension compiles against the host's entity assembly but not
// against the assembly its context lives in, and the host registers the context as the base type.
// The de-duplication and the ordering are the database's and nothing here collects. A set assembled
// in memory would cost one loaded row per identified scene, so it would grow with the library.
// The host's endpoint rule is applied in memory. Comparing the two spellings as strings would answer
// that a video the host treats as identified carries no identity.
internal sealed class LibrarySceneIdentityPort(DbContext db, OptionsStore options)
    : ILibrarySceneIdentityPort
{
    public async IAsyncEnumerable<string> SceneIdentities(
        WhisparrGeneration generation, [EnumeratorCancellation] CancellationToken ct)
    {
        var namespaced = await NamespacedFor(generation, ct).ConfigureAwait(false);

        var carried = db.Set<VideoRemoteId>()
            .AsNoTracking()
            .Select(row => new { row.Endpoint, row.RemoteId })
            .Distinct()
            .OrderBy(row => row.Endpoint)
            .ThenBy(row => row.RemoteId)
            .AsAsyncEnumerable();

        await foreach (var row in carried.WithCancellation(ct).ConfigureAwait(false))
        {
            if (EndpointMatchGuard.SameSource(row.Endpoint, namespaced)
                && !string.IsNullOrWhiteSpace(row.RemoteId))
            {
                yield return row.RemoteId;
            }
        }
    }

    // Derived as the library's scene count minus the videos an identity row names, because the
    // same-source rule cannot be expressed as a query. The rows arrive ordered by video, so a video
    // already counted is recognised from the one before it and the walk holds one row at a time.
    public async Task<int> CountUnidentifiedAsync(
        WhisparrGeneration generation, CancellationToken ct)
    {
        var namespaced = await NamespacedFor(generation, ct).ConfigureAwait(false);
        var scenes = await db.Set<Video>().AsNoTracking().CountAsync(ct).ConfigureAwait(false);

        var carried = db.Set<VideoRemoteId>()
            .AsNoTracking()
            .Select(row => new { row.VideoId, row.Endpoint, row.RemoteId })
            .Distinct()
            .OrderBy(row => row.VideoId)
            .AsAsyncEnumerable();

        var identified = 0;
        int? counted = null;
        await foreach (var row in carried.WithCancellation(ct).ConfigureAwait(false))
        {
            if (row.VideoId != counted
                && EndpointMatchGuard.SameSource(row.Endpoint, namespaced)
                && !string.IsNullOrWhiteSpace(row.RemoteId))
            {
                identified++;
                counted = row.VideoId;
            }
        }

        // Never below zero. The two reads are separate statements, so a video deleted between them
        // would otherwise make the skipped count read as a negative number on the page.
        return Math.Max(0, scenes - identified);
    }

    public async IAsyncEnumerable<LibrarySiteIdentity> SiteIdentities(
        WhisparrGeneration generation, [EnumeratorCancellation] CancellationToken ct)
    {
        var namespaced = await NamespacedFor(generation, ct).ConfigureAwait(false);

        var carried = db.Set<StudioRemoteId>()
            .AsNoTracking()
            .Select(row => new { row.StudioId, row.Endpoint, row.RemoteId })
            .Distinct()
            .OrderBy(row => row.StudioId)
            .ThenBy(row => row.Endpoint)
            .ThenBy(row => row.RemoteId)
            .AsAsyncEnumerable();

        await foreach (var row in carried.WithCancellation(ct).ConfigureAwait(false))
        {
            if (EndpointMatchGuard.SameSource(row.Endpoint, namespaced)
                && !string.IsNullOrWhiteSpace(row.RemoteId))
            {
                yield return new LibrarySiteIdentity(row.StudioId, row.RemoteId);
            }
        }
    }

    // Derived the way the scene count is. The rows arrive ordered by studio, so a studio already
    // counted is recognised from the one before it and the walk holds one row at a time.
    public async Task<int> CountUnidentifiedSitesAsync(
        WhisparrGeneration generation, CancellationToken ct)
    {
        var namespaced = await NamespacedFor(generation, ct).ConfigureAwait(false);
        var studios = await db.Set<Studio>().AsNoTracking().CountAsync(ct).ConfigureAwait(false);

        var carried = db.Set<StudioRemoteId>()
            .AsNoTracking()
            .Select(row => new { row.StudioId, row.Endpoint, row.RemoteId })
            .Distinct()
            .OrderBy(row => row.StudioId)
            .AsAsyncEnumerable();

        var identified = 0;
        int? counted = null;
        await foreach (var row in carried.WithCancellation(ct).ConfigureAwait(false))
        {
            if (row.StudioId != counted
                && EndpointMatchGuard.SameSource(row.Endpoint, namespaced)
                && !string.IsNullOrWhiteSpace(row.RemoteId))
            {
                identified++;
                counted = row.StudioId;
            }
        }

        // Never below zero, for the reason the scene count is not: the two reads are separate
        // statements, so a studio deleted between them would otherwise read as a negative number.
        return Math.Max(0, studios - identified);
    }

    private async Task<string> NamespacedFor(WhisparrGeneration generation, CancellationToken ct)
    {
        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        return IdentityEndpoint.PreferredFor(generation, stored.MetadataProviderEndpoints);
    }
}
