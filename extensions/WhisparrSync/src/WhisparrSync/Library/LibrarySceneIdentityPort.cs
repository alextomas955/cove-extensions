using System.Runtime.CompilerServices;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Options;

using EndpointMatchGuard = WhisparrSync.Import.EndpointMatchGuard;
using IdentityEndpoint = WhisparrSync.Import.IdentityEndpoint;

namespace WhisparrSync.Library;

/// <inheritdoc cref="ILibrarySceneIdentityPort"/>
/// <remarks>
/// Binds the base <see cref="DbContext"/> for the reason its siblings do: this extension compiles
/// against the host's entity assembly but not against the assembly its context lives in, and the
/// host registers that context resolvable as the base type.
/// <para>
/// The de-duplication and the ordering are the DATABASE's, and nothing in this file collects. A set
/// assembled here would cost one loaded row per identified scene, so on a library of millions it
/// would answer correctly and be unusable.
/// </para>
/// <para>
/// The endpoint rule is the host's own and is applied in memory. Comparing the two spellings as
/// strings would answer that a video the host itself treats as identified carries no identity, and
/// that scene would then be counted as one Whisparr cannot be told about.
/// </para>
/// </remarks>
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

    /// <inheritdoc/>
    /// <remarks>
    /// Derived as the library's own scene count minus the videos an identity row in the namespace
    /// names, because the same-source rule cannot be expressed as a query and a video's rows have to
    /// be read to apply it. The rows arrive ordered by video, so a video already counted is
    /// recognised from the one before it and the walk holds one identifier at a time.
    /// </remarks>
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

    /// <inheritdoc/>
    /// <remarks>
    /// Derived the way the scene count is, and for the same reason: the same-source rule cannot be
    /// expressed as a query. The rows arrive ordered by studio, so a studio already counted is
    /// recognised from the one before it and the walk holds one identifier at a time.
    /// </remarks>
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
