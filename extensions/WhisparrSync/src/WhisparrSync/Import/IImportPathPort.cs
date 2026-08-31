namespace WhisparrSync.Import;

/// <summary>What one candidate path is on disk.</summary>
/// <param name="Exists">Whether a file is there.</param>
/// <param name="Size">Its size in bytes, or null when there is no file to size.</param>
public sealed record ProbedPath(bool Exists, long? Size);

/// <summary>
/// The one seam through which this extension looks at the filesystem.
/// </summary>
/// <remarks>
/// Deliberately narrow: there is no method that moves, renames, deletes, opens or writes, so no call
/// site can express one. The product holds no capability to change what is in either system's
/// storage, and this is where that is structural rather than a promise.
/// <para>
/// The path it takes is always one <see cref="PathCandidateGuard"/> constructed. Nothing here
/// re-checks containment, so a caller handing it a reported string directly would be probing the
/// whole filesystem.
/// </para>
/// </remarks>
public interface IImportPathPort
{
    /// <summary>What is at <paramref name="path"/>, if anything.</summary>
    /// <remarks>
    /// Answers rather than throws when the path cannot be read at all - an unreadable path and an
    /// absent one are both "no file this product can verify", and the caller acts on neither.
    /// </remarks>
    ProbedPath Probe(string path);
}
