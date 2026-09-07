using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// Resolve ONE Cove scene's Whisparr movie row — v3-ONLY. v2 has no scene-level id (a scene exists only as
/// an episode under a site), the same reason the other v3-only roles exist, so a v2 adapter never
/// implements this role and a per-scene resolve structurally cannot be asked of one.
/// </summary>
internal interface IWhisparrSceneLookup
{
    /// <summary>
    /// Resolves the Whisparr movie for the scene carrying <paramref name="stashIds"/>, or <c>Ok(null)</c>
    /// when Whisparr holds no row for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The read is the per-id filter Whisparr offers, and despite its name that filter selects on the row's
    /// own <c>foreignId</c>, not on a StashDB id the row merely carries. So one row shape is invisible here:
    /// a row whose StashDB id matches but whose foreign id differs resolves to null, where a whole-set
    /// resolve would have found it. That narrowing is deliberate, and every caller's not-found branch is the
    /// declining one — nothing acts on a different movie because of it.
    /// </para>
    /// <para>
    /// Each answered row is passed through <see cref="SceneStatus.SceneStatusProjector.IsMovieFor"/>, the
    /// rule the whole-set index keys on, so the answer is a subset of a whole-set resolve and can never be a
    /// row that index would not have keyed.
    /// </para>
    /// <para>
    /// A scene carrying no usable id resolves to null with no outbound call. A failed read propagates its
    /// classification verbatim, so "Whisparr could not be asked" is never reported as "not added".
    /// </para>
    /// </remarks>
    Task<WhisparrResult<WhisparrMovie?>> FindSceneMovieAsync(
        string baseUrl,
        string apiKey,
        IReadOnlyList<string> stashIds,
        CancellationToken ct);
}
