using WhisparrSync.Contracts;

namespace WhisparrSync.Library;

/// <summary>The identifiers the library's own scenes are known by, across the whole library.</summary>
/// <remarks>
/// Streamed, never answered as a collection: libraries reach millions of files, so a materialized
/// answer would grow with the library.
/// <para>
/// A video carrying a link only in the other generation's namespace is not an identified scene here
/// and is not answered.
/// </para>
/// <para>
/// A caller needing the number of identifiers enumerates <see cref="SceneIdentities"/> and counts
/// what it yields, never a count query of its own. The host's same-source rule is applied after the
/// query's own <c>Distinct</c>, and two spellings of one source occur in real data, so a count taken
/// in the database answers a different number from the stream a run walks.
/// </para>
/// </remarks>
public interface ILibrarySceneIdentityPort
{
    /// <summary>
    /// The identifier every identified scene in the library carries in
    /// <paramref name="generation"/>'s namespace.
    /// </summary>
    /// <remarks>
    /// Every match is answered rather than one per video: several identifiers under one video are
    /// two spellings of one source, and the instance answers the second offer as a scene it holds.
    /// </remarks>
    IAsyncEnumerable<string> SceneIdentities(WhisparrGeneration generation, CancellationToken ct);

    /// <summary>
    /// The identifier each file directly in <paramref name="coveFolder"/> carries, by file name.
    /// </summary>
    /// <remarks>
    /// One row per identified file in that one folder, so nothing here grows with the library. The
    /// name rather than the whole path, because a caller pairs these against a listing the instance
    /// answered for the same folder under its own spelling of it.
    /// </remarks>
    IAsyncEnumerable<LibraryFileIdentity> FileIdentitiesIn(
        string coveFolder, WhisparrGeneration generation, CancellationToken ct);

    /// <summary>
    /// The same identifiers, each carried under one folder the library holds its files in, with
    /// every folder the library holds appearing whether or not an identifier was placed under it.
    /// </summary>
    /// <remarks>
    /// The root order is the library roots in the order their folders are to be walked, and a folder
    /// under none of them is walked after all of them.
    /// <para>
    /// Ordered by that and then by folder, so a caller walking this stream
    /// reaches a folder's identifiers together and knows the folder is finished when the next one
    /// arrives. A caller that puts the roots its instance can reach first therefore reaches
    /// something it can act on early. Streamed one row at a time for the reason
    /// <see cref="SceneIdentities"/> is.
    /// </para>
    /// <para>
    /// An identifier whose files sit in several folders is carried under the first of them in path
    /// order and under no other, and one the library holds no file for is carried under no folder
    /// at all, so the identifiers this member yields are the same set, and the same number, as
    /// <see cref="SceneIdentities"/> yields. The folders no identifier was carried under still
    /// arrive, because their files are attached by the entries their other folders registered.
    /// </para>
    /// <para>
    /// Path order decides which folder carries an identifier and the root order decides when that
    /// folder is reached, so a scene holding files under two roots can be reached
    /// at one of them before it has been registered under the other. Its file there attaches on the
    /// next run, once the entry exists.
    /// </para>
    /// </remarks>
    IAsyncEnumerable<LibrarySceneInFolder> SceneIdentitiesByFolder(
        WhisparrGeneration generation, IReadOnlyList<string> rootOrder, CancellationToken ct);

    /// <summary>
    /// How many of the library's scenes carry no identity row in <paramref name="generation"/>'s
    /// namespace.
    /// </summary>
    /// <remarks>One number whatever the library holds; what it counts is walked, never collected.</remarks>
    Task<int> CountUnidentifiedAsync(WhisparrGeneration generation, CancellationToken ct);

    /// <summary>
    /// Every studio the library holds that carries an identifier in
    /// <paramref name="generation"/>'s namespace.
    /// </summary>
    /// <remarks>
    /// Streamed and capped by nothing, for the reason <see cref="SceneIdentities"/> is. A caller
    /// needing the number of sites enumerates this member and counts what it yields.
    /// </remarks>
    IAsyncEnumerable<LibrarySiteIdentity> SiteIdentities(
        WhisparrGeneration generation, CancellationToken ct);

    /// <summary>
    /// How many of the library's studios carry no identity row in
    /// <paramref name="generation"/>'s namespace.
    /// </summary>
    /// <inheritdoc cref="CountUnidentifiedAsync" path="/remarks"/>
    Task<int> CountUnidentifiedSitesAsync(WhisparrGeneration generation, CancellationToken ct);
}

/// <summary>One identifier the library carries, and the folder it was placed under.</summary>
/// <remarks>A null folder means the library holds no file for it.</remarks>
internal sealed record PlacedIdentity(string Endpoint, string? RemoteId, string? Folder);

/// <summary>One file in a library folder, named, and the identifier its video carries.</summary>
public sealed record LibraryFileIdentity(string FileName, string RemoteId);

/// <summary>One library folder, and one identifier registered from it.</summary>
/// <remarks>
/// A null <c>RemoteId</c> means the folder holds files but no identifier was placed under it, so
/// there is nothing to register here and the folder is still linked.
/// <para>
/// A null <c>Folder</c> means the library holds no file for that identifier, so it is registered
/// and no folder is linked for it. Both are never null at once.
/// </para>
/// </remarks>
public sealed record LibrarySceneInFolder(string? Folder, string? RemoteId);

/// <summary>One studio the library holds, as the site pass addresses it.</summary>
/// <remarks>
/// Both identifiers travel together. Whisparr is asked about the remote id, and the read answering
/// that studio's own scenes is keyed by Cove's id, so a caller holding one and not the other would
/// have to walk the studios again, and a second walk answers a different set.
/// </remarks>
public sealed record LibrarySiteIdentity(int StudioId, string RemoteId);
