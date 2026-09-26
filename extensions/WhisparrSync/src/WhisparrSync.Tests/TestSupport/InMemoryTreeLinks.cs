using WhisparrSync.Import;
using WhisparrSync.Linking;

namespace WhisparrSync.Tests.TestSupport;

// The filesystem a host-level case runs against. Every path outside a tree is a file, which is what
// the seeded library is, and a path inside one is a file only once something linked it there.
//
// An identity is derived from the path rather than assigned, so two readings of one path agree and
// a link answers the identity of what it points at. The volume is the path's leading segment, which
// is how a case puts two roots on two devices: the drive letter or the first mount segment differs,
// and a link across them is refused the way the platform refuses one.
internal sealed class InMemoryTreeLinks : ITreeLinkPort
{
    private readonly Dictionary<string, string> _links = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _ignoreFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deleted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tree roots an ignore file was written at, and what it says.</summary>
    internal IReadOnlyDictionary<string, string> IgnoreFiles => _ignoreFiles;

    /// <summary>Every name this run put in a tree, against the file it points at.</summary>
    internal IReadOnlyDictionary<string, string> Links => _links;

    /// <summary>Takes the library's own name for <paramref name="path"/> away.</summary>
    /// <remarks>
    /// What a reader deleting a file leaves behind: a link in the tree is then the only name the
    /// file has, which is the one state a name may be taken back in.
    /// </remarks>
    internal void Forget(string path) => _deleted.Add(PathCandidateGuard.Normalize(path));

    public ProbedLink? Identify(string path)
    {
        var spelled = PathCandidateGuard.Normalize(path);
        if (_links.TryGetValue(spelled, out var pointsAt))
        {
            return Reading(pointsAt, _deleted.Contains(pointsAt) ? 1 : 2);
        }

        return InATree(spelled) ? null : Reading(spelled, names: 1);
    }

    public LinkOutcome Link(string existingPath, string newPath)
    {
        var source = PathCandidateGuard.Normalize(existingPath);
        var name = PathCandidateGuard.Normalize(newPath);

        if (_links.ContainsKey(name) || !InATree(name))
        {
            return LinkOutcome.NameAlreadyThere;
        }

        if (Identify(source) is null)
        {
            return LinkOutcome.SourceNotThere;
        }

        if (!string.Equals(VolumeOf(source), VolumeOf(name), StringComparison.OrdinalIgnoreCase))
        {
            return LinkOutcome.OnAnotherDevice;
        }

        _links[name] = source;
        return LinkOutcome.Linked;
    }

    public bool EnsureFolder(string path)
    {
        _folders.Add(PathCandidateGuard.Normalize(path));
        return true;
    }

    public IEnumerable<string> NamesIn(string folder)
    {
        var prefix = PathCandidateGuard.Normalize(folder).TrimEnd('/') + "/";

        return [.. _links.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => path[prefix.Length..])
            .Where(name => !name.Contains('/', StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];
    }

    public IEnumerable<string> FoldersIn(string folder)
    {
        var prefix = PathCandidateGuard.Normalize(folder).TrimEnd('/') + "/";

        return [.. _folders
            .Concat(_links.Keys)
            .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => path[prefix.Length..])
            .Where(tail => tail.Contains('/', StringComparison.Ordinal)
                || _folders.Contains(prefix + tail))
            .Select(tail => tail.Split('/', 2)[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)];
    }

    public NameRemoval Remove(string treeRoot, string path)
    {
        if (PathCandidateGuard.TailBelow(path, treeRoot) is null)
        {
            return NameRemoval.Refused;
        }

        return _links.Remove(PathCandidateGuard.Normalize(path))
            ? NameRemoval.Removed
            : NameRemoval.NotThere;
    }

    public bool WriteIgnore(string treeRoot)
    {
        _ignoreFiles[PathCandidateGuard.Normalize(treeRoot)] = "*";
        return true;
    }

    private static ProbedLink Reading(string path, int names)
        => new(
            new FileIdentity(Numbered(VolumeOf(path)), Numbered(path)),
            names,
            DateTimeOffset.UnixEpoch);

    // Anything below a folder named for one of the trees. A case seeds no library folder spelled
    // that way, so the test is exact here.
    private static bool InATree(string path)
        => path.Contains("/.wsync-", StringComparison.OrdinalIgnoreCase);

    private static string VolumeOf(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? string.Empty : segments[0];
    }

    private static ulong Numbered(string text)
    {
        var number = 14695981039346656037UL;
        foreach (var character in text)
        {
            number = (number ^ char.ToUpperInvariant(character)) * 1099511628211UL;
        }

        return number;
    }
}
