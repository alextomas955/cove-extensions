using WhisparrSync.Contracts;
using WhisparrSync.Import;

namespace WhisparrSync.Linking;

/// <summary>What one pass over the names in an entity's folder did with them.</summary>
/// <remarks>
/// Counts, never names. The folder holds one name per file the entity owns, and an entity reaches
/// the size of the library.
/// <para>
/// <c>Refused</c> covers the names this pass could settle nothing about: one whose identity could
/// not be read, one the composition would put outside the folder, and one the seam declined to
/// remove. Each leaves the name where it is, and none is retried inside the pass.
/// </para>
/// </remarks>
internal sealed record TreeSweep(
    int Removed,
    int StillNamedElsewhere,
    int WaitingToSettle,
    int NotComposedHere,
    int Refused)
{
    /// <summary>A pass that looked at no name.</summary>
    internal static TreeSweep Nothing { get; } = new(0, 0, 0, 0, 0);
}

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
    int Refused,
    TreeSweep Swept)
{
    /// <summary>A pass that built no folder and linked nothing.</summary>
    internal static TreeBuild Nothing { get; } = new(null, 0, 0, 0, 0, TreeSweep.Nothing);

    /// <summary>How many of the entity's files are reachable in the folder now.</summary>
    internal int LinksThere => Linked + AlreadyThere;
}

/// <summary>
/// Gives one entity a folder of its own, a second name in it for each of its library files, and
/// takes back the names whose files the library no longer holds.
/// </summary>
/// <remarks>
/// The pass reads one library file, acts on it and forgets it, and then reads one name in the
/// folder, decides it and forgets that. It holds no folder listing, no set of the entity's
/// identities and no record of what it linked, because each of those is a collection the size of
/// an entity, and an entity reaches the size of the library. Neither half needs the other's:
/// whether a name's file is still in the library is the name count from that name's own reading.
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
internal sealed class TreeReconcileStep(ITreeLinkPort links, TimeProvider clock)
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

        // Set where the read ends, and read by the pass below it. A read that stopped short
        // establishes nothing about the files it never reached, so it can say nothing about which
        // names have no library file left.
        var libraryFilesReadToTheEnd = false;

        try
        {
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

            libraryFilesReadToTheEnd = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped rather than failed: what was linked before the stop is a second name for a
            // file the library holds and there is nothing to undo. The pass answers what it did,
            // and the caller's next act on the same token is what ends the run.
        }

        return new TreeBuild(
            entityFolder,
            linked,
            alreadyThere,
            onAnotherDevice,
            refused,
            Swept(treeRoot, entityFolder, libraryFilesReadToTheEnd, ct));
    }

    // One name at a time, straight off the seam's listing: the name is decided from its own
    // identity reading and dropped, so a folder holding a library's worth of names costs the same
    // per name as a folder holding one.
    //
    // Every removal this extension issues is here, behind TreeLinkRemovalGuard, and each one
    // happens inside a pass that goes on to report what it did, so there is no state in which names
    // went and nothing said so.
    private TreeSweep Swept(
        string treeRoot,
        string entityFolder,
        bool libraryFilesReadToTheEnd,
        CancellationToken ct)
    {
        // Checked here as well as inside the guard, for the reason the seam's own removal re-checks
        // its tree root: this is the one act in this product that cannot be undone. A pass that
        // stopped short does not even list the folder.
        if (!libraryFilesReadToTheEnd)
        {
            return TreeSweep.Nothing;
        }

        // One reading for the folder. A clock read per name would put the names at the end of a
        // long listing under a window a fraction wider than the ones at its start.
        var now = clock.GetUtcNow();
        var removed = 0;
        var stillNamedElsewhere = 0;
        var waitingToSettle = 0;
        var notComposedHere = 0;
        var refused = 0;

        foreach (var name in links.NamesIn(entityFolder))
        {
            ct.ThrowIfCancellationRequested();

            if (PathCandidateGuard.CandidateUnder(entityFolder, name) is not { } namePath)
            {
                refused++;
                continue;
            }

            var verdict = TreeLinkRemovalGuard.Decide(
                entityFolder, namePath, links.Identify(namePath), libraryFilesReadToTheEnd, now);

            switch (verdict.Kept)
            {
                case TreeNameKept.NotComposedHere:
                    notComposedHere++;
                    continue;
                case TreeNameKept.StillNamedElsewhere:
                    stillNamedElsewhere++;
                    continue;
                case TreeNameKept.WaitingToSettle:
                    waitingToSettle++;
                    continue;
                case { }:
                    refused++;
                    continue;
                default:
                    break;
            }

            // A name nothing holds by the time the removal is issued is counted with the refusals
            // rather than with what this pass freed: the listing and the removal are two moments,
            // and only the first of them is this pass's own work.
            if (links.Remove(treeRoot, namePath) is NameRemoval.Removed)
            {
                removed++;
            }
            else
            {
                refused++;
            }
        }

        return new TreeSweep(
            removed, stillNamedElsewhere, waitingToSettle, notComposedHere, refused);
    }
}
