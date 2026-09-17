using WhisparrSync.Contracts;

namespace WhisparrSync.Monitoring;

/// <summary>Where one entity's or one video's own files sit, as the library holds them.</summary>
/// <remarks>
/// Streamed rather than answered as a collection. A library reaches millions of files, so a caller
/// reads one folder at a time and hands that folder's rows straight into one request; a materialized
/// answer would grow with the library whatever the caller then did with it.
/// </remarks>
public interface IEntityFolderPort
{
    /// <summary>
    /// The distinct folders the <paramref name="kind"/> entity <paramref name="coveId"/> names holds
    /// files in, in path order.
    /// </summary>
    /// <remarks>
    /// A folder appears once however many of the entity's files sit in it, and the de-duplication is
    /// the database's rather than the caller's. An id below one answers nothing, because there is no
    /// entity for it to be about.
    /// <para>
    /// A blank path is never answered. A caller hands each path straight into a request that names a
    /// directory to read, and a blank one names none.
    /// </para>
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
    /// One scalar, answered by the database, so the cost of asking does not grow with the number of
    /// files under the root. The narrowing is on the denormalized path column the host stores and
    /// indexes rather than on the folder row, which is what makes the read an index seek.
    /// <para>
    /// The root's trailing separator is part of the prefix, so a sibling directory whose name begins
    /// with the root's own name is not under it. An id below one answers zero, because there is no
    /// entity for it to be about.
    /// </para>
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
    /// The same scalar read as the entity-keyed count, narrowed to one video. A caller adding a
    /// single scene has a video and no owning entity in hand, and the scene's own file is better
    /// evidence of where it sits than its studio is: a studio split across roots would send the
    /// scene to the root holding the majority of the studio's other files rather than to the one
    /// holding this scene's.
    /// <para>
    /// An id below one answers zero, because there is no video for it to be about.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="coveRoot"/> is blank.</exception>
    Task<int> VideoFilesUnderAsync(int videoId, string coveRoot, CancellationToken ct);
}
