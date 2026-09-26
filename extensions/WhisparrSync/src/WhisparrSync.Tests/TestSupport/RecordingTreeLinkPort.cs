using WhisparrSync.Import;
using WhisparrSync.Linking;

namespace WhisparrSync.Tests.TestSupport;

/// <summary>One call a caller made through the linking seam, with everything it carried.</summary>
public sealed record TreeLinkCall(string Verb, string Path, string? Other = null);

// Stands in for the one seam that changes a reader's disk. It refuses a call it was not arranged
// for rather than answering a default: a double that reports nothing at an unplaced path is how a
// pass that looked at the wrong path reads as a pass with nothing to do.
//
// The number of names is derived from what was placed rather than taken from the test, because it
// is the one answer every removal here turns on, and a test that could state it could state the
// case it was written to prove.
internal sealed class RecordingTreeLinkPort : ITreeLinkPort
{
    private readonly Dictionary<string, PlacedFile> _placed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinkOutcome> _linkAnswers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unreadable = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _refusedRemovals = new(StringComparer.OrdinalIgnoreCase);

    public List<TreeLinkCall> Calls { get; } = [];

    public List<string> Folders { get; } = [];

    public List<string> IgnoredTrees { get; } = [];

    /// <summary>Puts a file at <paramref name="path"/> with an identity of its own.</summary>
    public void Place(string path, FileIdentity identity, DateTimeOffset changed)
        => _placed[Spelled(path)] = new PlacedFile(identity, changed);

    /// <summary>Puts a second name at <paramref name="path"/> for the file already at <paramref name="ofPath"/>.</summary>
    public void PlaceLink(string path, string ofPath)
        => _placed[Spelled(path)] = Held(ofPath, "place a link to");

    /// <summary>Makes a link attempt at <paramref name="newPath"/> answer <paramref name="outcome"/>.</summary>
    public void AnswerLink(string newPath, LinkOutcome outcome)
        => _linkAnswers[Spelled(newPath)] = outcome;

    /// <summary>Makes an identity read at <paramref name="path"/> answer nothing.</summary>
    public void AnswerNothingFor(string path) => _unreadable.Add(Spelled(path));

    /// <summary>Takes the name at <paramref name="path"/> away, as a reader deleting it does.</summary>
    /// <remarks>
    /// Not a call through the seam and not recorded as one: it is how a test states what happened
    /// to a reader's library between two passes.
    /// </remarks>
    public void Forget(string path) => _placed.Remove(Spelled(path));

    /// <summary>Makes a removal at <paramref name="path"/> answer that the name is still there.</summary>
    public void RefuseRemovalOf(string path) => _refusedRemovals.Add(Spelled(path));

    public ProbedLink? Identify(string path)
    {
        Calls.Add(new TreeLinkCall("identify", path));

        if (_unreadable.Contains(Spelled(path)))
        {
            return null;
        }

        var file = Held(path, "identify");
        return new ProbedLink(file.Identity, NamesOf(file.Identity), file.Changed);
    }

    public LinkOutcome Link(string existingPath, string newPath)
    {
        Calls.Add(new TreeLinkCall("link", newPath, existingPath));

        // Derived from what is there rather than taken, the way the name count is: a name already
        // held is the answer a second pass over one library turns on, and a test that could state
        // it could state the case it was written to prove.
        if (_placed.ContainsKey(Spelled(newPath)))
        {
            return LinkOutcome.NameAlreadyThere;
        }

        if (!_linkAnswers.TryGetValue(Spelled(newPath), out var outcome))
        {
            throw new InvalidOperationException($"No link outcome was arranged for {newPath}.");
        }

        if (outcome is LinkOutcome.Linked)
        {
            _placed[Spelled(newPath)] = Held(existingPath, "link");
        }

        return outcome;
    }

    // Nothing arranges a folder: this double holds no folders of its own, and what a caller does
    // with the answer is the subject rather than which folders a disk already had.
    public bool EnsureFolder(string path)
    {
        Calls.Add(new TreeLinkCall("folder", path));
        Folders.Add(path);
        return true;
    }

    public IEnumerable<string> NamesIn(string folder)
    {
        Calls.Add(new TreeLinkCall("names", folder));

        var prefix = Spelled(folder).TrimEnd('/') + "/";
        return _placed.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => path[prefix.Length..])
            .Where(name => !name.Contains('/', StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    public NameRemoval Remove(string treeRoot, string path)
    {
        Calls.Add(new TreeLinkCall("remove", path, treeRoot));

        if (PathCandidateGuard.TailBelow(path, treeRoot) is null
            || _refusedRemovals.Contains(Spelled(path)))
        {
            return NameRemoval.Refused;
        }

        return _placed.Remove(Spelled(path)) ? NameRemoval.Removed : NameRemoval.NotThere;
    }

    public bool WriteIgnore(string treeRoot)
    {
        Calls.Add(new TreeLinkCall("ignore", treeRoot));
        IgnoredTrees.Add(treeRoot);
        return true;
    }

    private static string Spelled(string path) => PathCandidateGuard.Normalize(path);

    private PlacedFile Held(string path, string act)
        => _placed.TryGetValue(Spelled(path), out var file)
            ? file
            : throw new InvalidOperationException($"Nothing was placed at {path} to {act}.");

    private int NamesOf(FileIdentity identity)
        => _placed.Values.Count(file => file.Identity == identity);

    private sealed record PlacedFile(FileIdentity Identity, DateTimeOffset Changed);
}
