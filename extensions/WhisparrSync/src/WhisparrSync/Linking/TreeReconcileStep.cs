using WhisparrSync.Contracts;

namespace WhisparrSync.Linking;

/// <summary>What one pass over an entity's files left in the folder this extension owns for it.</summary>
/// <remarks>
/// Counts and the one folder, never a list of files. A studio is an entity, and on a library whose
/// files all sit together one studio owns every one of them, so a member holding an entry per file
/// would grow with the library.
/// <para>
/// A null <c>EntityFolder</c> is a pass that built no folder, so the entity is registered the way
/// it was before this capability existed.
/// </para>
/// </remarks>
internal sealed record TreeBuild(
    string? EntityFolder,
    int Linked,
    int AlreadyThere,
    int OnAnotherDevice,
    int Refused)
{
    /// <summary>A pass that built no folder and linked nothing.</summary>
    internal static TreeBuild Nothing { get; } = new(null, 0, 0, 0, 0);

    /// <summary>How many of the entity's files are reachable in the folder now.</summary>
    internal int LinksThere => Linked + AlreadyThere;
}

/// <summary>
/// Gives one entity a folder of its own and a second name in it for each of its library files.
/// </summary>
/// <remarks>
/// The pass reads one library file, acts on it and forgets it. It holds no folder listing, no set
/// of the entity's identities and no record of what it linked, because each of those is a
/// collection the size of an entity, and an entity reaches the size of the library.
/// <para>
/// That is affordable because a link is named after the identity of the file it points at. The name
/// a library file's link should have is computable from that file alone, so one library file costs
/// one identity read and one link attempt whatever else the folder holds. A file either side
/// renamed keeps its identity, so its name is unchanged, the attempt answers that the name is
/// already there, and no second name is made.
/// </para>
/// <para>
/// The attempt is what asks the question. A reading taken first and a link made after would answer
/// about two moments, and the link itself already refuses to overwrite.
/// </para>
/// <para>
/// One instance per run. It remembers which tree roots it has written the host's ignore file at, so
/// a run over an entity per scene writes one per library root rather than one per entity. The set
/// is bounded by the configured root count.
/// </para>
/// </remarks>
internal sealed class TreeReconcileStep(ITreeLinkPort links)
{
    private readonly HashSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Builds the folder <paramref name="remoteId"/> names under <paramref name="coveRoot"/> and
    /// links each of <paramref name="libraryFiles"/> into it.
    /// </summary>
    /// <remarks>
    /// Every file is one the entity owns under <paramref name="coveRoot"/>. A file under another
    /// root is on another device as far as this is concerned: the link is refused and counted, and
    /// nothing is copied.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="coveRoot"/> or <paramref name="remoteId"/> is blank.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="libraryFiles"/> is null.</exception>
    internal async Task<TreeBuild> BuildAsync(
        string coveRoot,
        WhisparrGeneration generation,
        string remoteId,
        IAsyncEnumerable<string> libraryFiles,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coveRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);
        ArgumentNullException.ThrowIfNull(libraryFiles);

        if (TreePathGuard.TreeRootUnder(coveRoot, generation) is not { } treeRoot
            || TreePathGuard.EntityFolderIn(treeRoot, remoteId) is not { } entityFolder)
        {
            return TreeBuild.Nothing;
        }

        // Before anything is linked, and at the tree root rather than in the entity folder: the
        // file keeps the host's scan out of everything below it, and a link that landed first
        // could be discovered as a second copy of a file the library already holds.
        if (_ignored.Add(treeRoot) && !links.WriteIgnore(treeRoot))
        {
            return TreeBuild.Nothing;
        }

        if (!links.EnsureFolder(entityFolder))
        {
            return TreeBuild.Nothing;
        }

        var linked = 0;
        var alreadyThere = 0;
        var onAnotherDevice = 0;
        var refused = 0;

        await foreach (var libraryFile in libraryFiles.WithCancellation(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            if (links.Identify(libraryFile) is not { } file
                || TreePathGuard.LinkPathIn(entityFolder, file.Identity, libraryFile)
                    is not { } linkPath)
            {
                refused++;
                continue;
            }

            switch (links.Link(libraryFile, linkPath))
            {
                case LinkOutcome.Linked:
                    linked++;
                    break;
                case LinkOutcome.NameAlreadyThere:
                    alreadyThere++;
                    break;
                case LinkOutcome.OnAnotherDevice:
                    onAnotherDevice++;
                    break;
                default:
                    refused++;
                    break;
            }
        }

        return new TreeBuild(entityFolder, linked, alreadyThere, onAnotherDevice, refused);
    }
}
