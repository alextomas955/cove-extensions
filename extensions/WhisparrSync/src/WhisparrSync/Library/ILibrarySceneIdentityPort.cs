using WhisparrSync.Contracts;

namespace WhisparrSync.Library;

/// <summary>The identifiers the library's own scenes are known by, across the whole library.</summary>
/// <remarks>
/// Streamed rather than answered as a collection, for the reason the entity-scoped read is: a
/// library reaches millions of files, so a caller reads one identifier at a time and hands it into
/// one bounded batch. A materialized answer would grow with the library whatever the caller then
/// did.
/// <para>
/// The namespace is chosen by the connected generation rather than by preference. A video carrying a
/// link only in the other generation's namespace is not an identified scene here at all, so it is
/// not answered and nothing about it leaves.
/// </para>
/// <para>
/// A caller needing the NUMBER of identifiers must enumerate <see cref="SceneIdentities"/> and count
/// what it yields, rather than issue a count query of its own. The same-source rule is the host's
/// and is applied after the query's own <c>Distinct</c>, and two spellings of one source are present
/// in real data, so a count taken in the database answers a different number from the stream a run
/// then walks.
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
    /// two spellings of one source rather than an ambiguity, and the instance answers the second
    /// offer as a scene it already holds.
    /// </remarks>
    IAsyncEnumerable<string> SceneIdentities(WhisparrGeneration generation, CancellationToken ct);

    /// <summary>
    /// How many of the library's scenes carry no identity row in <paramref name="generation"/>'s
    /// namespace.
    /// </summary>
    /// <remarks>
    /// A scalar, so the answer is one number whatever the library holds. What it is derived from is
    /// walked rather than collected, for the reason the stream above is.
    /// </remarks>
    Task<int> CountUnidentifiedAsync(WhisparrGeneration generation, CancellationToken ct);

    /// <summary>
    /// Every studio the library holds that carries an identifier in
    /// <paramref name="generation"/>'s namespace.
    /// </summary>
    /// <remarks>
    /// Streamed and capped by nothing, for the reason <see cref="SceneIdentities"/> is. A caller
    /// needing the number of sites enumerates this same member and counts what it yields.
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
/// Both identifiers travel together. The identifier is what a Whisparr is asked about, and Cove's
/// own id is what the read answering that studio's own scenes is keyed by, so a caller holding one
/// and not the other would have to walk the studios a second time to reach the scenes under them -
/// and a second walk answers a different set from the one the run walked.
/// </remarks>
/// <param name="StudioId">Cove's own id for the studio.</param>
/// <param name="RemoteId">The identifier the studio carries in the connected namespace.</param>
public sealed record LibrarySiteIdentity(int StudioId, string RemoteId);
