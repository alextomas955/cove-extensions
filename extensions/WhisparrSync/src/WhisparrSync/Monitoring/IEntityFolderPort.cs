using WhisparrSync.Contracts;

namespace WhisparrSync.Monitoring;

/// <summary>Where one entity's or one video's own files sit, as the library holds them.</summary>
/// <remarks>
/// Streamed, never answered as a collection: a library reaches millions of files, so a materialized
/// answer would grow with the library.
/// </remarks>
public interface IEntityFolderPort
{
    /// <summary>
    /// The distinct folders the <paramref name="kind"/> entity <paramref name="coveId"/> names holds
    /// files in, in path order.
    /// </summary>
    /// <remarks>
    /// The database de-duplicates and orders, so nothing here grows with the library. An id below one
    /// answers nothing. A blank path is never answered: a caller hands each path straight into a
    /// request that names a directory to read, and a blank one names none.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a kind this product expresses.
    /// </exception>
    IAsyncEnumerable<string> FoldersFor(WhisparrEntityKind kind, int coveId, CancellationToken ct);

    /// <summary>
    /// How many files the <paramref name="kind"/> entity <paramref name="coveId"/> names holds under
    /// <paramref name="coveRoot"/>.
    /// </summary>
    /// <remarks>
    /// One scalar, answered by the database, so the cost does not grow with the number of files under
    /// the root. The root's trailing separator is part of the prefix, so a sibling directory whose
    /// name begins with the root's own name is not under it. An id below one answers zero.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a kind this product expresses.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="coveRoot"/> is blank.</exception>
    Task<int> FilesUnderAsync(
        WhisparrEntityKind kind, int coveId, string coveRoot, CancellationToken ct);

    /// <summary>
    /// How many files the video <paramref name="videoId"/> names holds under
    /// <paramref name="coveRoot"/>.
    /// </summary>
    /// <remarks>
    /// The same scalar read, narrowed to one video. A caller adding a single scene has no owning
    /// entity in hand, and a studio split across roots would place the scene where most of the
    /// studio's other files sit rather than where this scene's file sits. An id below one answers
    /// zero.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="coveRoot"/> is blank.</exception>
    Task<int> VideoFilesUnderAsync(int videoId, string coveRoot, CancellationToken ct);
}
