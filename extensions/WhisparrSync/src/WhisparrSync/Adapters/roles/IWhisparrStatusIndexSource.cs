using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// The whole-library scene-status index, read as a narrow projection folded row by row.
/// </summary>
/// <remarks>
/// Keyed by the scene-level ids a Cove scene carries, so only a generation whose rows carry one can answer it.
/// The older generation's rows are synthesized per site and carry a null stash id under an item type the keying
/// rule does not read, so every one of them indexes under NO key: the index it can build is empty for a library
/// of any size, and a consumer partitioning scenes by state cannot tell that apart from "Whisparr has none of
/// these scenes". So the role is declared by <see cref="V3Adapter"/> alone and is NOT on the shared
/// <see cref="IWhisparrAdapter"/> aggregate — the same rule the rest of the per-scene surface follows, and for
/// the same missing id. A consumer narrows to it (<c>adapter is IWhisparrStatusIndexSource</c>) and refuses when
/// it is absent; there is no probe and no version test.
/// <para>
/// Keying the older generation's rows on the ThePornDB id they DO carry would let it answer, and would move the
/// counts a user sees, so it is a capability decision with its own verification rather than a cleanup: a TPDB id
/// compared against a StashDB id is the cross-match this keying rule exists to prevent.
/// </para>
/// </remarks>
internal interface IWhisparrStatusIndexSource
{
    /// <summary>
    /// The status index keyed by every StashDB-comparable id a scene could match, built without the movie-row
    /// array ever existing.
    /// </summary>
    Task<WhisparrResult<IReadOnlyDictionary<string, WhisparrMovieFacts>>> LoadStatusIndexAsync(
        string baseUrl, string apiKey, CancellationToken ct);
}
