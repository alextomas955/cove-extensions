using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Scene;

// The acting surface is split by entity kind and by verb, so a capability a generation cannot
// honour is a role it holds no registration for rather than a check inside one wide role.

/// <summary>Monitors one scene the connected instance already holds.</summary>
/// <remarks>
/// Narrow in the same way the entity acting roles are: no member takes a caller-supplied route and
/// none takes an HTTP verb. The identifier arrives already resolved off the instance's own row for
/// the scene, so nothing here can be aimed by an identifier a browser supplied.
/// <para>
/// Both generations register it, so the member takes one, and each composes the flag in its own
/// shape. Nothing declared here can make an instance download: the verbs that can are on the
/// grabbing roles, each of which a caller has to obtain by name.
/// </para>
/// </remarks>
public interface IWhisparrSceneMonitorActing
{
    /// <summary>Sets only the monitored flag on the scene <paramref name="sceneId"/> names.</summary>
    /// <remarks>
    /// Every other field the instance holds for that scene is left unset, and an unset field is not
    /// applied. Setting the flag false governs what a later catalogue addition does and retracts
    /// nothing already downloaded.
    /// <para>
    /// <paramref name="sceneId"/> is the instance's own identifier for the scene on whichever
    /// generation is named: the catalogue item's id on one, and the row's id under its site on the
    /// other.
    /// </para>
    /// </remarks>
    Task<WhisparrResponse> SetSceneMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int sceneId,
        bool monitored,
        CancellationToken ct);
}

/// <summary>Excludes one scene from what the connected instance will take.</summary>
/// <remarks>
/// A writing role of its own rather than a widening of the exclusion read: the safety invariants
/// classify per member, and a role whose name says reading is what lets a caller hold it without
/// holding a write.
/// <para>
/// Narrow in the same way the other acting roles are: no member takes a caller-supplied route and
/// none takes an HTTP verb. Only v3 registers it, so neither member takes a
/// generation, and the identifiers arrive already resolved off a stored identity row or off the
/// instance's own exclusion list.
/// </para>
/// </remarks>
public interface IWhisparrSceneExclusionActing
{
    /// <summary>Excludes the scene <paramref name="foreignId"/> names.</summary>
    /// <remarks>
    /// Governs what a later catalogue addition takes and retracts nothing the instance already
    /// holds. Sent once, like every acting request, and it issues no search.
    /// </remarks>
    Task<WhisparrResponse> AddSceneExclusionAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct);

    /// <summary>Removes the exclusion <paramref name="exclusionId"/> names.</summary>
    /// <remarks>
    /// Addressed by the exclusion row's own identifier and not by the scene's foreign id, because
    /// that is what the removing route names. A caller reads the identifier off the instance's own
    /// exclusion list first.
    /// </remarks>
    Task<WhisparrResponse> RemoveSceneExclusionAsync(
        Uri baseAddress, string apiKey, int exclusionId, CancellationToken ct);
}
