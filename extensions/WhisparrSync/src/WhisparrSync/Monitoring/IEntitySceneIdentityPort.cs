using WhisparrSync.Contracts;

namespace WhisparrSync.Monitoring;

/// <summary>The identifiers one entity's own scenes are known by, as the library holds them.</summary>
/// <remarks>
/// Streamed, never answered as a collection: a library reaches millions of files, so a materialized
/// answer would grow with the library.
/// <para>
/// The namespace is the connected generation's. A video carrying a link only in the other
/// generation's namespace is not an identified scene here, so it is not answered.
/// </para>
/// </remarks>
public interface IEntitySceneIdentityPort
{
    /// <summary>
    /// The identifiers the scenes of the <paramref name="kind"/> entity <paramref name="coveId"/>
    /// names carry in <paramref name="generation"/>'s namespace.
    /// </summary>
    /// <remarks>
    /// Every match is answered, not one: several identifiers under one entity are its catalogue
    /// rather than an ambiguity. An id below one answers nothing.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a kind this product expresses.
    /// </exception>
    IAsyncEnumerable<string> SceneIdentitiesFor(
        WhisparrEntityKind kind, int coveId, WhisparrGeneration generation, CancellationToken ct);
}
