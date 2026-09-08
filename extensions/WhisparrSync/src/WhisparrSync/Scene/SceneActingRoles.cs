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
/// Only the newer generation registers it, so the member takes no generation. Nothing declared here
/// can make an instance download: the verbs that can are on the grabbing roles, each of which a
/// caller has to obtain by name.
/// </para>
/// </remarks>
public interface IWhisparrSceneMonitorActing
{
    /// <summary>Sets only the monitored flag on the scene <paramref name="sceneId"/> names.</summary>
    /// <remarks>
    /// Every other field the instance holds for that scene is left unset, and an unset field is not
    /// applied. Setting the flag false governs what a later catalogue addition does and retracts
    /// nothing already downloaded.
    /// </remarks>
    Task<WhisparrResponse> SetSceneMonitoredAsync(
        Uri baseAddress, string apiKey, int sceneId, bool monitored, CancellationToken ct);
}
