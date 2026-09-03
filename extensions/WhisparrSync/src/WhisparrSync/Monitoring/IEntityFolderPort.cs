namespace WhisparrSync.Monitoring;

/// <summary>The folders one entity's own files sit in, as the library holds them.</summary>
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
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a kind this product expresses.
    /// </exception>
    IAsyncEnumerable<string> FoldersFor(WhisparrEntityKind kind, int coveId, CancellationToken ct);
}
