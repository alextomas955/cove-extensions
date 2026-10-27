namespace WhisparrSync.Whisparr;

/// <summary>Adds a studio so the instance tracks its catalogue, wanting none of it.</summary>
/// <remarks>
/// The surface that reads what a reader is missing needs the instance to list the entity's scenes,
/// and an instance lists none for an entity it has never been told about. This add is what makes
/// that list exist without asking for a single download.
/// <para>
/// Distinct from monitoring the entity, which is a request for its scenes. Nothing added this way is
/// wanted, nothing is searched, and a reader monitors what they choose afterwards, one scene or a
/// selection at a time.
/// </para>
/// <para>
/// One role per entity kind, because what the add composes differs per kind and per generation: on
/// v2 a site is the only unit of presence the instance holds, so a studio is the only kind it can be
/// told about at all. A kind with no role here is a kind no instance can be told about, and the
/// surface refuses it by obtaining no role rather than by naming a generation.
/// </para>
/// </remarks>
public interface IWhisparrStudioCatalogueTracking
{
    /// <summary>Adds <paramref name="foreignId"/> so its catalogue is tracked and wanted by nothing.</summary>
    Task<WhisparrResponse> TrackStudioCatalogueAsync(
        string foreignId, AddDefaults defaults, CancellationToken ct);
}

/// <summary>Adds a performer so the instance tracks their catalogue, wanting none of it.</summary>
/// <remarks>The studio role carries why this add exists and what it is not.</remarks>
public interface IWhisparrPerformerCatalogueTracking
{
    /// <summary>Adds <paramref name="foreignId"/> so its catalogue is tracked and wanted by nothing.</summary>
    Task<WhisparrResponse> TrackPerformerCatalogueAsync(
        string foreignId, AddDefaults defaults, CancellationToken ct);
}
