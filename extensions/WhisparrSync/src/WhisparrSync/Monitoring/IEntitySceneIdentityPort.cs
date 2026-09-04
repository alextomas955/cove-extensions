using WhisparrSync.Contracts;

namespace WhisparrSync.Monitoring;

/// <summary>The identifiers one entity's own scenes are known by, as the library holds them.</summary>
/// <remarks>
/// Streamed rather than answered as a collection, for the reason the folder source is: a library
/// reaches millions of files, so a caller reads one identifier at a time and hands it straight into
/// one request. A materialized answer would grow with the library whatever the caller then did.
/// <para>
/// The namespace is chosen by the connected generation rather than by preference. A video carrying a
/// link only in the other generation's namespace is not an identified scene here at all, so it is
/// not answered and nothing about it leaves.
/// </para>
/// </remarks>
public interface IEntitySceneIdentityPort
{
    /// <summary>
    /// The identifiers the scenes of the <paramref name="kind"/> entity <paramref name="coveId"/>
    /// names carry in <paramref name="generation"/>'s namespace.
    /// </summary>
    /// <remarks>
    /// Every match is answered rather than one, which is where this differs from the entity identity
    /// read: several identifiers under one entity are its catalogue rather than an ambiguity.
    /// <para>
    /// An id below one answers nothing, because there is no entity for it to be about.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a kind this product expresses.
    /// </exception>
    IAsyncEnumerable<string> SceneIdentitiesFor(
        WhisparrEntityKind kind, int coveId, WhisparrGeneration generation, CancellationToken ct);
}
