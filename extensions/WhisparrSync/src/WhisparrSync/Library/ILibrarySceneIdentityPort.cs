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

/// <summary>One studio the library holds, as the site pass addresses it.</summary>
/// <remarks>
/// Both identifiers travel together. Whisparr is asked about the remote id, and the read answering
/// that studio's own scenes is keyed by Cove's id, so a caller holding one and not the other would
/// have to walk the studios again, and a second walk answers a different set.
/// </remarks>
public sealed record LibrarySiteIdentity(int StudioId, string RemoteId);
