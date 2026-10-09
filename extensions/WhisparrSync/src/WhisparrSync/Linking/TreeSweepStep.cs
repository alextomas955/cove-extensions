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

    /// <summary>Folds one folder's pass into the total a pass over the tree carries.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="other"/> is null.</exception>
    internal TreeSweep Plus(TreeSweep other)
    {
        ArgumentNullException.ThrowIfNull(other);

        return new TreeSweep(
            Removed + other.Removed,
            StillNamedElsewhere + other.StillNamedElsewhere,
            WaitingToSettle + other.WaitingToSettle,
            NotComposedHere + other.NotComposedHere,
            Refused + other.Refused);
    }
}

/// <summary>
/// Takes back the names in a tree whose library files the library no longer holds.
/// </summary>
/// <remarks>
/// Its own pass, apart from the one that builds an entity's folder, because the two are reached
/// differently. A folder is built for an entity the library offers, which is an entity that owns
/// files; the names to take back are in the tree whether the library still offers their entity or
/// not, and the case a removal exists for is a reader deleting an entity's last file, which drops
/// that entity out of the library altogether.
/// <para>
/// It reads one name, decides it and forgets it, holding no folder listing and no set of any
/// entity's identities: each of those is a collection the size of an entity, and an entity reaches
/// the size of the library. Whether a name's file is still in the library is the name count from
/// that name's own reading, which is one call about one name.
/// </para>
/// </remarks>
internal sealed class TreeSweepStep(ITreeLinkPort links, TimeProvider clock)
{
    /// <summary>Takes back the names in every tree under <paramref name="coveRoots"/>.</summary>
    /// <remarks>
    /// Over the folders the tree itself holds, not over the entities the library offered.
    /// <para>
    /// <paramref name="libraryReadToTheEnd"/> is whether the run reached the end of the library. A
    /// run that stopped short lists nothing and removes nothing: it establishes nothing about the
    /// files it never reached, so it can say nothing about which names have no library file left.
    /// </para>
    /// <para>
    /// A folder the tree holds that no entity of this product's is kept in is passed over like any
    /// other: every name in it is decided by the same check, and a name this extension did not
    /// compose is left where it is. That is what a folder the instance built for itself inside the
    /// tree is made of.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="coveRoots"/> is null.</exception>
    internal TreeSweep Over(
        IReadOnlyList<string> coveRoots,
        WhisparrGeneration generation,
        bool libraryReadToTheEnd,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(coveRoots);

        // Checked here as well as inside the check, for the reason the seam's own removal re-checks
        // its tree root: this is the one act in this product that cannot be undone. A run that
        // stopped short does not even list a tree.
        if (!libraryReadToTheEnd)
        {
            return TreeSweep.Nothing;
        }

        var swept = TreeSweep.Nothing;
        try
        {
            foreach (var coveRoot in coveRoots)
            {
                if (TreePathGuard.TreeRootUnder(coveRoot, generation) is { } treeRoot)
                {
                    swept = swept.Plus(Swept(treeRoot, libraryReadToTheEnd, ct));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Stopped rather than failed. A name already taken back was one no library file
            // answered to, so there is nothing to undo, and the pass answers what it did.
        }

        return swept;
    }

    // Every folder in one tree, including the ones this product did not make: both generations
    // import into a folder of their own naming, and the check leaves every name in one of those
    // where it is.
    private TreeSweep Swept(string treeRoot, bool libraryReadToTheEnd, CancellationToken ct)
    {
        var swept = TreeSweep.Nothing;
        foreach (var folder in links.FoldersIn(treeRoot))
        {
            ct.ThrowIfCancellationRequested();

            if (PathCandidateGuard.CandidateUnder(treeRoot, folder) is { } entityFolder)
            {
                swept = swept.Plus(
                    SweptFolder(treeRoot, entityFolder, libraryReadToTheEnd, ct));
            }
        }

        return swept;
    }

    // One name at a time, straight off the seam's listing: the name is decided from its own
    // identity reading and dropped, so a folder holding a library's worth of names costs the same
    // per name as a folder holding one.
    //
    // Every removal this extension issues is here, behind TreeLinkRemovalGuard, and each one
    // happens inside a pass that goes on to report what it did, so there is no state in which names
    // went and nothing said so.
    private TreeSweep SweptFolder(
        string treeRoot,
        string entityFolder,
        bool libraryReadToTheEnd,
        CancellationToken ct)
    {
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
                entityFolder, namePath, links.Identify(namePath), libraryReadToTheEnd, now);

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
