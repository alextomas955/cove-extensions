using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

/// <summary>
/// Adds an entity so the instance tracks its catalogue, wanting none of it.
/// </summary>
/// <remarks>
/// The surface that reads what a reader is missing needs the instance to list the entity's scenes,
/// and an instance lists none for an entity it has never been told about. This add is what makes
/// that list exist without asking for a single download.
/// <para>
/// Distinct from monitoring the entity, which is a request for its scenes. Nothing added this way is
/// wanted, nothing is searched, and a reader monitors what they choose afterwards, one scene or a
/// selection at a time.
/// </para>
/// </remarks>
public interface IWhisparrEntityTrackingActing
{
    /// <summary>Adds <paramref name="foreignId"/> so its catalogue is tracked and wanted by nothing.</summary>
    Task<WhisparrResponse> TrackEntityAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct);
}
