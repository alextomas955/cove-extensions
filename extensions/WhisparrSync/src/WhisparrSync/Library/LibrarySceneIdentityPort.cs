using System.Runtime.CompilerServices;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Identity;
using WhisparrSync.Import;
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

    // Two ordered reads walked in step rather than one joined query: the identity half has to be
    // filtered by the same-source rule in memory, which a join would have to apply before it could
    // decide whether a folder carries an identifier at all.
    //
    // Each identifier is placed under the first folder its files sit in, so the set and the count
    // this yields are the identity stream's own. Both reads are narrowed to one root at a time and
    // ordered by path, so they meet in step and the walk holds one row from each however large the
    // library is.
    public async IAsyncEnumerable<LibraryFileIdentity> FileIdentitiesIn(
        string coveFolder,
        WhisparrGeneration generation,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coveFolder);

        var namespaced = await NamespacedFor(generation, ct).ConfigureAwait(false);

        var carried = db.Set<VideoFile>()
            .AsNoTracking()
            .Where(file => file.ParentFolder!.Path == coveFolder)
            .Join(
                db.Set<VideoRemoteId>().AsNoTracking(),
                file => file.VideoId,
                row => row.VideoId,
                (file, row) => new { file.Path, row.Endpoint, row.RemoteId })
            .Distinct()
            .AsAsyncEnumerable();

        await foreach (var row in carried.WithCancellation(ct).ConfigureAwait(false))
        {
            if (Offerable(row.Endpoint, row.RemoteId, namespaced))
            {
                yield return new LibraryFileIdentity(NameOf(row.Path), row.RemoteId);
            }
        }
    }

    // The last segment, over the stored forward-slash spelling.
    private static string NameOf(string path)
    {
        var normalized = PathCandidateGuard.Normalize(path);
        var cut = normalized.LastIndexOf('/');
        return cut < 0 ? normalized : normalized[(cut + 1)..];
    }

    public IAsyncEnumerable<LibrarySceneInFolder> SceneIdentitiesByFolder(
        WhisparrGeneration generation, IReadOnlyList<string> rootOrder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rootOrder);
        return ByFolderAsync(generation, rootOrder, ct);
    }

    private async IAsyncEnumerable<LibrarySceneInFolder> ByFolderAsync(
        WhisparrGeneration generation,
        IReadOnlyList<string> rootOrder,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var namespaced = await NamespacedFor(generation, ct).ConfigureAwait(false);

        // Offered before any folder, so an identifier the library holds no file for is registered
        // whatever the walk reaches afterwards. It links nothing: there is no folder to link.
        await foreach (var row in Placed().WithCancellation(ct).ConfigureAwait(false))
        {
            if (row.Folder is null && Offerable(row.Endpoint, row.RemoteId, namespaced))
            {
                yield return new LibrarySceneInFolder(null, row.RemoteId);
            }
        }

        foreach (var root in rootOrder)
        {
            var under = root;
            await foreach (var placed in WalkAsync(
                folder => PathCandidateGuard.IsAtOrBelow(folder, under),
                namespaced,
                ct).ConfigureAwait(false))
            {
                yield return placed;
            }
        }

        await foreach (var placed in WalkAsync(
            folder => !rootOrder.Any(root => PathCandidateGuard.IsAtOrBelow(folder, root)),
            namespaced,
            ct).ConfigureAwait(false))
        {
            yield return placed;
        }
    }

    // The folder read and the identifier read, both ordered by path and both narrowed by the same
    // condition here rather than in the query, so they stay in step and nothing outlives one folder.
    // The narrowing is applied here because the aggregate that places an identifier cannot be
    // filtered afterwards: one of the two providers refuses to translate that at all.
    private async IAsyncEnumerable<LibrarySceneInFolder> WalkAsync(
        Func<string, bool> includes,
        string namespaced,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var identities = Placed().GetAsyncEnumerator(ct);
        var held = await MoveToAPlacedRowAsync(identities, includes).ConfigureAwait(false);

        await foreach (var folder in FolderPaths().AsAsyncEnumerable()
            .WithCancellation(ct).ConfigureAwait(false))
        {
            if (!includes(folder))
            {
                continue;
            }

            var carriedHere = false;

            // Ahead of the folder only where a row was filtered out of the folder read; the two
            // reads are otherwise over the same set.
            while (held
                && string.Compare(identities.Current.Folder, folder, StringComparison.Ordinal) < 0)
            {
                held = await MoveToAPlacedRowAsync(identities, includes).ConfigureAwait(false);
            }

            while (held
                && string.Equals(identities.Current.Folder, folder, StringComparison.Ordinal))
            {
                var row = identities.Current;
                if (Offerable(row.Endpoint, row.RemoteId, namespaced))
                {
                    carriedHere = true;
                    yield return new LibrarySceneInFolder(folder, row.RemoteId);
                }

                held = await MoveToAPlacedRowAsync(identities, includes).ConfigureAwait(false);
            }

            if (!carriedHere)
            {
                yield return new LibrarySceneInFolder(folder, null);
            }
        }
    }

    // Skips rows this walk is not about, so the reader only ever sees rows whose folder the folder
    // read will also reach.
    private static async ValueTask<bool> MoveToAPlacedRowAsync(
        IAsyncEnumerator<PlacedIdentity> identities, Func<string, bool> includes)
    {
        while (await identities.MoveNextAsync().ConfigureAwait(false))
        {
            if (identities.Current.Folder is { } folder && includes(folder))
            {
                return true;
            }
        }

        return false;
    }

    // Joined rather than selected per row: the correlated form translates to SQL APPLY, which one of
    // the two providers this runs on refuses outright. Left, so an identifier the library holds no
    // file for keeps its place and is still offered; the aggregate passes over the empty side, so an
    // identifier held on two videos, one of them without files, still takes the folder of the one
    // that has them.
    private IAsyncEnumerable<PlacedIdentity> Placed()
        => db.Set<VideoRemoteId>()
            .AsNoTracking()
            .GroupJoin(
                db.Set<VideoFile>()
                    .AsNoTracking()
                    .Where(file => !string.IsNullOrWhiteSpace(file.ParentFolder!.Path)),
                row => row.VideoId,
                file => file.VideoId,
                (row, matched) => new { row, matched })
            .SelectMany(
                joined => joined.matched.DefaultIfEmpty(),
                (joined, file) => new
                {
                    joined.row.Endpoint,
                    joined.row.RemoteId,
                    Folder = file == null ? null : file.ParentFolder!.Path,
                })
            .GroupBy(row => new { row.Endpoint, row.RemoteId })
            .Select(group => new
            {
                group.Key.Endpoint,
                group.Key.RemoteId,
                Folder = group.Min(row => row.Folder),
            })
            .OrderBy(row => row.Folder)
            .ThenBy(row => row.Endpoint)
            .ThenBy(row => row.RemoteId)
            .AsAsyncEnumerable()
            .Select(row => new PlacedIdentity(row.Endpoint, row.RemoteId, row.Folder));

    private IQueryable<string> FolderPaths()
        => db.Set<VideoFile>()
            .AsNoTracking()
            .Where(file => !string.IsNullOrWhiteSpace(file.ParentFolder!.Path))
            .Select(file => file.ParentFolder!.Path)
            .Distinct()
            .OrderBy(path => path);

    private static bool Offerable(string endpoint, string? remoteId, string namespaced)
        => EndpointMatchGuard.SameSource(endpoint, namespaced)
            && !string.IsNullOrWhiteSpace(remoteId);

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
