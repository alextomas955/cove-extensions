namespace WhisparrSync.Import;

/// <summary>What one candidate path is on disk.</summary>
/// <remarks>The size is null when there is no file to size.</remarks>
public sealed record ProbedPath(bool Exists, long? Size);

/// <summary>
/// The one seam through which this extension looks at the filesystem.
/// </summary>
/// <remarks>
/// Narrow on purpose: no method moves, renames, deletes, opens or writes, so no call site can
/// express one.
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
    /// Answers rather than throws when the path cannot be read: an unreadable path and an absent
    /// one are both no file this product can verify.
    /// </remarks>
    ProbedPath Probe(string path);
}
