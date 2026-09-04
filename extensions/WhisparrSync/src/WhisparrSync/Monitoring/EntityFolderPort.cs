using System.Runtime.CompilerServices;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace WhisparrSync.Monitoring;

/// <inheritdoc cref="IEntityFolderPort"/>
/// <remarks>
/// Binds the base <see cref="DbContext"/> for the same reason the identity port does: this extension
/// compiles against the host's entity assembly but not against the assembly its context lives in,
/// and the host registers that context resolvable as the base type.
/// <para>
/// The de-duplication and the ordering are the DATABASE's. A set assembled here would be one entry
/// per distinct folder and still cost one loaded row per file to build, so on a library of millions
/// it would answer correctly and be unusable. Nothing in this file may collect.
/// </para>
/// <para>
/// The path answered is the folder's own, never the denormalized full path a file carries: the
/// instance is asked to parse a directory, and a file path names no directory to read.
/// </para>
/// <para>
/// A blank path is excluded IN the query, for the same reason the de-duplication is: a filter over
/// the materialized sequence would load every row to drop a few. The consumer that reads a folder,
/// <c>ListImportableFilesAsync</c>, refuses a blank one with an <see cref="ArgumentException"/>,
/// which no exception filter on the route and no catch in the run contains, so a blank row reaching
/// it faults the whole run instead of skipping one folder. On a selection that is every entity
/// after the first losing its outcome.
/// </para>
/// </remarks>
internal sealed class EntityFolderPort(DbContext db) : IEntityFolderPort
{
    public async IAsyncEnumerable<string> FoldersFor(
        WhisparrEntityKind kind, int coveId, [EnumeratorCancellation] CancellationToken ct)
    {
        // The kind is read before the id is, so an unexpressible kind is a fault rather than an
        // entity holding no files: the two answers mean different things and only one is about the
        // library.
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

    /// <summary>The video files one entity holds, as a query.</summary>
    /// <remarks>
    /// A studio's files reach it through the column its videos carry; a performer's reach it through
    /// the join table, which no studio row appears in. Neither is reachable from the other's entity
    /// without a navigation walk over loaded rows.
    /// </remarks>
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
