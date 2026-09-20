using System.Runtime.CompilerServices;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Import;

namespace WhisparrSync.Monitoring;

// Binds the base DbContext because this extension compiles against the host's entity assembly but
// not against the assembly its context lives in, and the host registers that context resolvable as
// the base type.
internal sealed class EntityFolderPort(DbContext db) : IEntityFolderPort
{
    public async IAsyncEnumerable<string> FoldersFor(
        WhisparrEntityKind kind, int coveId, [EnumeratorCancellation] CancellationToken ct)
    {
        var files = FilesOf(kind, coveId);
        if (coveId < 1)
        {
            yield break;
        }

        var folders = files
            .AsNoTracking()
            .Where(file => !string.IsNullOrWhiteSpace(file.ParentFolder!.Path))
            .Select(file => file.ParentFolder!.Path)
            .Distinct()
            .OrderBy(path => path)
            .AsAsyncEnumerable();

        await foreach (var folder in folders.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return folder;
        }
    }

    public async Task<int> FilesUnderAsync(
        WhisparrEntityKind kind, int coveId, string coveRoot, CancellationToken ct)
    {
        var files = FilesOf(kind, coveId);
        ArgumentException.ThrowIfNullOrWhiteSpace(coveRoot);
        if (coveId < 1)
        {
            return 0;
        }

        // Stored paths are the forward-slash form, so a root configured with the other separator
        // matches nothing without this. The trailing separator keeps a sibling whose name begins with
        // the root's own name out of the prefix match.
        var prefix = PathCandidateGuard.Normalize(coveRoot).TrimEnd('/') + "/";

        return await files
            .AsNoTracking()
            .Where(file => file.Path.StartsWith(prefix))
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<int> VideoFilesUnderAsync(
        int videoId, string coveRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coveRoot);
        if (videoId < 1)
        {
            return 0;
        }

        var prefix = PathCandidateGuard.Normalize(coveRoot).TrimEnd('/') + "/";

        return await db.Set<VideoFile>()
            .AsNoTracking()
            .Where(file => file.VideoId == videoId && file.Path.StartsWith(prefix))
            .CountAsync(ct)
            .ConfigureAwait(false);
    }

    // A studio's files are reached through the column its videos carry; a performer's through the
    // join table, which holds no studio row. Neither query reaches the other's entity.
    private IQueryable<VideoFile> FilesOf(WhisparrEntityKind kind, int coveId)
        => kind switch
        {
            WhisparrEntityKind.Studio => db.Set<VideoFile>()
                .Where(file => file.Video!.StudioId == coveId),
            WhisparrEntityKind.Performer => db.Set<VideoFile>()
                .Where(file => db.Set<VideoPerformer>()
                    .Any(linked => linked.PerformerId == coveId && linked.VideoId == file.VideoId)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This is not an entity kind this product expresses."),
        };
}
