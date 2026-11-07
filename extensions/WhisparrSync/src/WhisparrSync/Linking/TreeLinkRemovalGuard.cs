using WhisparrSync.Import;

namespace WhisparrSync.Linking;

/// <summary>Why one name in an entity's folder was left where it is.</summary>
internal enum TreeNameKept
{
    /// <summary>This extension did not compose the name, so the file is somebody else's.</summary>
    NotComposedHere,

    /// <summary>The file still answers to another name, so the library has not let go of it.</summary>
    StillNamedElsewhere,

    /// <summary>The file changed more recently than the settle window.</summary>
    WaitingToSettle,

    /// <summary>The name is not directly inside the entity's own folder.</summary>
    OutsideTheEntityFolder,

    /// <summary>Nothing could be read at the name.</summary>
    IdentityCouldNotBeRead,

    /// <summary>The run never reached the end of the library.</summary>
    LibraryNotReadToTheEnd,
}

/// <summary>What may be done with one name in an entity's folder.</summary>
internal sealed record TreeNameVerdict(bool Removable, TreeNameKept? Kept)
{
    internal static TreeNameVerdict MayBeRemoved { get; } = new(true, null);

    internal static TreeNameVerdict Keep(TreeNameKept why) => new(false, why);
}

/// <summary>Whether one name in an entity's folder may be removed.</summary>
/// <remarks>
/// Removing a name frees the file's bytes once it is the last one, so this is the only check in
/// this product whose wrong answer destroys a reader's media. It rests on one rule: a name this
/// extension did not compose is never removed.
/// <para>
/// That rule is load-bearing because this extension only ever makes second names. Every name it
/// writes is another name for a file the library already holds, so removing one can only take back
/// something it added. A name it did not write is somebody else's, and in this folder it is that
/// file's only copy: the host's scan is kept out of the tree by design, so a file put here by
/// anything else has no library row, never pairs with one, and stays unpaired however long it sits.
/// </para>
/// <para>
/// The question is answerable from the name alone because a link is spelled for the identity of the
/// file it points at, and nothing else in the folder carries its own identity as its name. A file
/// the instance downloaded here keeps the name the instance gave it, and a file a reader dropped
/// here keeps theirs.
/// </para>
/// <para>
/// Whether the library still holds the file is the name count from the same reading: a link this
/// extension composed has the library's own name beside it while the library holds the file, and
/// only itself when it does not. That is one call about one name and no collection, which is what
/// lets a studio owning the whole library be passed over one name at a time.
/// </para>
/// <para>
/// Pure, and takes no collection. A later change that hands it one is the change this unit exists
/// to prevent: a set of the entity's identities is a set the size of the library.
/// </para>
/// </remarks>
internal static class TreeLinkRemovalGuard
{
    /// <summary>How long after a file last changed its name in the tree is left alone.</summary>
    /// <remarks>
    /// It reduces churn where a file leaves the library and comes back: a rebuilt library row, a
    /// root remounted mid-scan, a file restored from a bin. Within the window the pass reports the
    /// name as waiting and removes nothing, so the next run decides with both readings behind it.
    /// <para>
    /// It is not what protects a file the instance has just downloaded, and it could not be. How
    /// long a delivery takes to arrive and be placed is a reader-editable setting
    /// (<see cref="Options.WhisparrSyncOptions.BackstopIntervalSeconds"/>), and a restart or a
    /// backed-up queue puts one outside any interval a constant could name. A download is safe
    /// because its name is not one this extension composed, which is true at every age.
    /// </para>
    /// </remarks>
    internal static readonly TimeSpan SettleWindow = TimeSpan.FromHours(1);

    /// <summary>What may be done with <paramref name="namePath"/> in <paramref name="entityFolder"/>.</summary>
    /// <remarks>
    /// <paramref name="read"/> is the identity read at the name itself, null where nothing could be
    /// read there. <paramref name="libraryReadToTheEnd"/> is whether the run reached the end of the
    /// library: a run that stopped short establishes nothing about the files it never reached, so
    /// it can say nothing about what has no library file left.
    /// <paramref name="now"/> is the run's own clock reading, taken once for the folder.
    /// </remarks>
    /// <exception cref="ArgumentException">Either path argument is blank.</exception>
    internal static TreeNameVerdict Decide(
        string entityFolder,
        string namePath,
        ProbedLink? read,
        bool libraryReadToTheEnd,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(namePath);

        if (!libraryReadToTheEnd)
        {
            return TreeNameVerdict.Keep(TreeNameKept.LibraryNotReadToTheEnd);
        }

        if (NameDirectlyIn(entityFolder, namePath) is not { } name)
        {
            return TreeNameVerdict.Keep(TreeNameKept.OutsideTheEntityFolder);
        }

        if (read is not { } file)
        {
            return TreeNameVerdict.Keep(TreeNameKept.IdentityCouldNotBeRead);
        }

        if (!TreePathGuard.IsComposedName(name, file.Identity))
        {
            return TreeNameVerdict.Keep(TreeNameKept.NotComposedHere);
        }

        // More than one name is the library's own name still there, which is what a rename on
        // either side and a move within one drive both leave behind.
        if (file.Names > 1)
        {
            return TreeNameVerdict.Keep(TreeNameKept.StillNamedElsewhere);
        }

        return now - file.Changed < SettleWindow
            ? TreeNameVerdict.Keep(TreeNameKept.WaitingToSettle)
            : TreeNameVerdict.MayBeRemoved;
    }

    // The entity folder itself, the tree root above it, another entity's folder and anything nested
    // below this one all answer null. A name is one segment directly inside the folder and nothing
    // else, which is what the listing this is driven from answers with.
    private static string? NameDirectlyIn(string entityFolder, string namePath)
        => PathCandidateGuard.TailBelow(namePath, entityFolder) is { Length: > 0 } tail
            && !tail.Contains('/', StringComparison.Ordinal)
                ? tail
                : null;
}
