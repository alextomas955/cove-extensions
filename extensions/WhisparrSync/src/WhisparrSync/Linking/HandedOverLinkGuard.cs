using WhisparrSync.Import;

namespace WhisparrSync.Linking;

/// <summary>
/// Whether a delivery names a link this extension handed the instance rather than a new arrival.
/// </summary>
/// <remarks>
/// The linking half gives the instance a second name for a file the library already holds. The
/// instance takes that file into its own catalogue and reports it back through the callback, where
/// it looks exactly like any other import it performed. It is not one. The bytes are already in the
/// library under the reader's own name, so placing the reported path and registering it adds a
/// second row for one file, and that row carries none of what the reader named the first by.
/// <para>
/// The name answers it on its own. A link is spelled for the identity of the file it points at, and
/// nothing else in a tree carries its own identity as its name: a file the instance downloaded
/// keeps the name the instance gave it, and a file a reader dropped in keeps theirs. That is the
/// same reading <see cref="TreeLinkRemovalGuard"/> turns on, and it is why a genuine download
/// reaching a tree is still an arrival here and still reaches the library.
/// </para>
/// <para>
/// One name and one reading, so it costs the same whatever the library, the entity or the tree
/// holds. The reading is taken through a delegate and asked for only where it can change the
/// answer, so a delivery from outside every tree pays for no filesystem call.
/// </para>
/// </remarks>
internal static class HandedOverLinkGuard
{
    /// <summary>Whether <paramref name="path"/> is a link this extension composed.</summary>
    /// <remarks>
    /// <paramref name="read"/> answers what is at a path, and null where nothing could be read
    /// there. A name whose file cannot be read settles nothing, so it is not one of ours.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
    /// <exception cref="ArgumentNullException">Any other argument is null.</exception>
    internal static bool NamesALinkComposedHere(
        string path, IReadOnlyList<string> coveRoots, Func<string, ProbedLink?> read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(coveRoots);
        ArgumentNullException.ThrowIfNull(read);

        return TreePathGuard.IsInsideATree(path, coveRoots)
            && read(path) is { } file
            && TreePathGuard.IsComposedName(PathCandidateGuard.LeafOf(path), file.Identity);
    }
}
