namespace WhisparrSync.Linking;

/// <summary>What the host's filesystem calls one file: the volume it is on and its number there.</summary>
/// <remarks>
/// Neither half identifies a file alone, because a file number is unique only within its volume.
/// The pair survives a rename and a move within the volume, and a copy of a file carries a
/// different pair, so it is what two systems holding the same bytes under different names pair on.
/// </remarks>
public readonly record struct FileIdentity(ulong Volume, ulong Number);

/// <summary>What one path is, read in a single call.</summary>
/// <remarks>
/// Equality is <see cref="Identity"/> alone. Two readings of one file taken at different moments
/// are the same file whatever the other two members say, and a reading of a copy is a different
/// file however closely the two agree.
/// <para>
/// <c>Names</c> is how many names the file has at the moment of the reading. A removal frees the
/// bytes only at one, so it is the member that decides whether removing a name destroys a file.
/// </para>
/// <para>
/// <c>Changed</c> is when the file's contents were last written, which is the one time both
/// platforms report for a file rather than for a name.
/// </para>
/// </remarks>
public sealed record ProbedLink(FileIdentity Identity, int Names, DateTimeOffset Changed)
{
    /// <inheritdoc/>
    public bool Equals(ProbedLink? other) => other is not null && Identity == other.Identity;

    /// <inheritdoc/>
    public override int GetHashCode() => Identity.GetHashCode();
}

/// <summary>What came of asking for a second name for a file.</summary>
public enum LinkOutcome
{
    /// <summary>The new name is there and names the same file.</summary>
    Linked,

    /// <summary>Something already holds the new name, and it was left as it was.</summary>
    NameAlreadyThere,

    /// <summary>There is no file at the path the new name was to be made for.</summary>
    SourceNotThere,

    /// <summary>
    /// The two paths are on separate devices, where no second name for one file can exist.
    /// </summary>
    /// <remarks>
    /// Its own outcome because the alternative a caller reaches for is a copy, which is a second
    /// set of the bytes rather than a second name for them.
    /// </remarks>
    OnAnotherDevice,

    /// <summary>The platform refused, and no name was made.</summary>
    Refused,
}

/// <summary>What came of asking to remove one name.</summary>
public enum NameRemoval
{
    /// <summary>The name is gone.</summary>
    Removed,

    /// <summary>Nothing held the name.</summary>
    NotThere,

    /// <summary>The name is still there: it lies outside the tree, or the platform refused.</summary>
    Refused,
}

/// <summary>
/// The one seam through which this extension changes the host's filesystem.
/// </summary>
/// <remarks>
/// Narrow on purpose. It copies nothing, moves nothing, renames nothing, opens no file's contents
/// and walks nothing below the folder it was given, so no call site can express any of those.
/// <para>
/// A file reaches a second folder by being given a second name there, never by being copied: a copy
/// is a second set of a reader's bytes on their disk, and the two then drift apart. That is why
/// there is no copy member and why a link across devices is refused rather than served some other
/// way.
/// </para>
/// <para>
/// Nothing here renames, because a name this extension made carries the identity of the file it
/// names, and that identity does not change. A file needing a different name needs a new one made
/// and the old one removed, which are two acts a caller has to write out.
/// </para>
/// <para>
/// The listing answers the names directly inside one folder and never descends, so no caller can
/// walk a reader's library through this seam, and it is streamed so that a folder's contents are
/// never held whole.
/// </para>
/// <para>
/// Every path handed in is one <see cref="Import.PathCandidateGuard"/> composed. A removal takes
/// the tree root beside the path and is refused outside it, checked here as well as at the call
/// site, because a removal is the one act here that cannot be undone.
/// </para>
/// <para>
/// Members answer rather than throw. A path that cannot be read and a path with nothing at it are
/// the same thing to act on.
/// </para>
/// </remarks>
public interface ITreeLinkPort
{
    /// <summary>What the file at <paramref name="path"/> is, or null where nothing could be read.</summary>
    /// <remarks>
    /// One call, because the identity and the number of names it carries have to describe one
    /// moment: a second reading for the count would answer about a file that may have gained or
    /// lost a name since.
    /// </remarks>
    ProbedLink? Identify(string path);

    /// <summary>
    /// Gives the file at <paramref name="existingPath"/> a second name at <paramref name="newPath"/>.
    /// </summary>
    /// <remarks>
    /// Never overwrites: a name already held answers <see cref="LinkOutcome.NameAlreadyThere"/> and
    /// whatever holds it is left alone. The folder above the new name has to be there already.
    /// </remarks>
    LinkOutcome Link(string existingPath, string newPath);

    /// <summary>Makes the folder at <paramref name="path"/>, and answers whether it is there after.</summary>
    /// <remarks>A folder already there is not an error.</remarks>
    bool EnsureFolder(string path);

    /// <summary>The names directly inside <paramref name="folder"/>.</summary>
    /// <remarks>
    /// Leaf names rather than paths, streamed as the platform answers them, and empty for a folder
    /// that is not there or cannot be read.
    /// </remarks>
    IEnumerable<string> NamesIn(string folder);

    /// <summary>The folders directly inside <paramref name="folder"/>.</summary>
    /// <remarks>
    /// Leaf names rather than paths, streamed as the platform answers them, and empty for a folder
    /// that is not there or cannot be read. Folders alone, because a tree root holds the file that
    /// keeps the host's scan out beside the folders the entities are kept in, and a pass over the
    /// entities has no business with the file.
    /// </remarks>
    IEnumerable<string> FoldersIn(string folder);

    /// <summary>Removes the name at <paramref name="path"/>, which must be below <paramref name="treeRoot"/>.</summary>
    /// <remarks>
    /// Removes a name and never a file: the bytes survive as long as another name points at them.
    /// A path that is not below the root answers <see cref="NameRemoval.Refused"/> with nothing
    /// touched.
    /// </remarks>
    NameRemoval Remove(string treeRoot, string path);

    /// <summary>Writes the file that keeps the host's own scan out of the tree at <paramref name="treeRoot"/>.</summary>
    /// <remarks>
    /// Answers whether that file is there afterwards. A file already saying what it should is left
    /// untouched, so a run does not rewrite a reader's disk to change nothing.
    /// </remarks>
    bool WriteIgnore(string treeRoot);
}
