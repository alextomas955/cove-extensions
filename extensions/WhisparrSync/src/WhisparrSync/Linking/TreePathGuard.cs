using System.Security.Cryptography;
using System.Text;
using WhisparrSync.Contracts;
using WhisparrSync.Import;

namespace WhisparrSync.Linking;

/// <summary>Where a tree, an entity's folder inside it and one link name are.</summary>
/// <remarks>
/// A link is named after the identity of the file it points at rather than after that file's own
/// name, and two things follow that the rest of this capability depends on.
/// <para>
/// The name a library file's link should have is computable from that file alone, so a pass asks
/// about one name instead of holding a folder's contents to search it. A rename on either side
/// leaves the name alone, because a rename does not change the identity, so one file never
/// collects a second link.
/// </para>
/// <para>
/// Whether a name in an entity folder is one this extension composed is answerable from that name
/// and the file at it alone. The spelling is the identity, and a file put there by anything else
/// does not carry its own identity as its name. That is the question a removal turns on.
/// </para>
/// <para>
/// The extension of the library file is carried onto the link, because it is what makes the name a
/// media file to both products, and it is the only part of a link name that is not the identity. A
/// library file that later changes extension gains a second name under the new one and keeps the
/// first: both are names for one file and neither is a second set of the bytes.
/// </para>
/// <para>
/// Pure, and performs no I/O. Every path is composed through <see cref="PathCandidateGuard"/>, so a
/// composition that would leave the folder it was built under answers null here as it does there.
/// </para>
/// </remarks>
public static class TreePathGuard
{
    // One tree per generation, because two generations group the same files differently and a
    // folder holds one entity.
    internal const string V3TreeFolder = ".wsync-v3";
    internal const string V2TreeFolder = ".wsync-v2";

    // The host's scan honours this file in any directory and applies it to everything below it.
    internal const string IgnoreFileName = ".coveignore";

    // Long enough for the identifiers both generations issue, short enough to leave room under the
    // path length a filesystem will take.
    private const int FolderNameCap = 64;

    private const int MarkerHexLength = 8;

    /// <summary>Where <paramref name="generation"/> keeps its tree under <paramref name="coveRoot"/>.</summary>
    /// <remarks>Null where the composition would leave the library root.</remarks>
    /// <exception cref="ArgumentException"><paramref name="coveRoot"/> is blank.</exception>
    public static string? TreeRootUnder(string coveRoot, WhisparrGeneration generation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(coveRoot);

        return PathCandidateGuard.CandidateUnder(coveRoot, TreeFolderOf(generation));
    }

    /// <summary>Where the entity <paramref name="remoteId"/> names keeps its links.</summary>
    /// <remarks>
    /// The folder name is the identifier with every character a folder name should not carry
    /// replaced, capped, and marked with a short digest of the identifier wherever the result is
    /// not the identifier itself. Two identifiers therefore never share a folder, which they must
    /// not: entities sharing one would attach each other's files. Null where the composition would
    /// leave the tree.
    /// </remarks>
    /// <exception cref="ArgumentException">Either argument is blank.</exception>
    public static string? EntityFolderIn(string treeRoot, string remoteId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteId);

        return PathCandidateGuard.CandidateUnder(treeRoot, FolderNameFor(remoteId));
    }

    /// <summary>What the link to <paramref name="libraryFilePath"/> is called in <paramref name="entityFolder"/>.</summary>
    /// <remarks>Null where the composition would leave the folder.</remarks>
    /// <exception cref="ArgumentException">Either path argument is blank.</exception>
    public static string? LinkPathIn(
        string entityFolder, FileIdentity identity, string libraryFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryFilePath);

        return PathCandidateGuard.CandidateUnder(
            entityFolder,
            Spelled(identity) + Path.GetExtension(PathCandidateGuard.Normalize(libraryFilePath)));
    }

    /// <summary>Whether <paramref name="name"/> is one this extension composed for <paramref name="identity"/>.</summary>
    /// <remarks>
    /// <paramref name="identity"/> is the identity of the file the name actually holds, read at the
    /// name itself. A name whose spelling is any other file's identity, or no identity at all, is
    /// something else's and is never removed.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="name"/> is blank.</exception>
    public static bool IsComposedName(string name, FileIdentity identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var extension = Path.GetExtension(name);
        return extension.Length > 1
            && string.Equals(
                name[..^extension.Length], Spelled(identity), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="path"/> sits inside a tree under one of <paramref name="coveRoots"/>.</summary>
    /// <remarks>
    /// A tree is at the top of a library root and nowhere else, so a library folder merely named
    /// like one is not a tree.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="coveRoots"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.</exception>
    public static bool IsInsideATree(string path, IReadOnlyList<string> coveRoots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(coveRoots);

        return coveRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .SelectMany(root => new[]
            {
                TreeRootUnder(root, WhisparrGeneration.V3),
                TreeRootUnder(root, WhisparrGeneration.V2),
            })
            .OfType<string>()
            .Any(tree => PathCandidateGuard.TailBelow(path, tree) is not null);
    }

    /// <summary>Where the file keeping the host's own scan out of <paramref name="treeRoot"/> sits.</summary>
    /// <remarks>Null where the composition would leave the tree.</remarks>
    /// <exception cref="ArgumentException"><paramref name="treeRoot"/> is blank.</exception>
    public static string? IgnoreFileIn(string treeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(treeRoot);

        return PathCandidateGuard.CandidateUnder(treeRoot, IgnoreFileName);
    }

    private static string TreeFolderOf(WhisparrGeneration generation)
        => generation is WhisparrGeneration.V2 ? V2TreeFolder : V3TreeFolder;

    // A file number means nothing without the volume it is on, so both halves are spelled.
    private static string Spelled(FileIdentity identity)
        => identity.Volume.ToString("x", System.Globalization.CultureInfo.InvariantCulture)
            + "-"
            + identity.Number.ToString("x", System.Globalization.CultureInfo.InvariantCulture);

    private static string FolderNameFor(string remoteId)
    {
        var replaced = new StringBuilder(remoteId.Length);
        foreach (var character in remoteId)
        {
            replaced.Append(Carried(character) ? character : '-');
        }

        var derived = replaced.ToString();
        var capped = derived.Length > FolderNameCap ? derived[..FolderNameCap] : derived;

        // A name of nothing but dots is the folder itself or the one above it, which would leave an
        // entity's links loose in the tree where nothing says whose they are. Marked like any other
        // name that is not its identifier.
        return string.Equals(capped, remoteId, StringComparison.Ordinal) && capped.Trim('.').Length > 0
            ? capped
            : capped + "-" + Marker(remoteId);
    }

    // Over the whole identifier rather than the capped spelling, so two identifiers that differ
    // only past the cap are marked differently.
    private static string Marker(string remoteId)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(remoteId)))[..MarkerHexLength];

    private static bool Carried(char character)
        => char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_';
}
