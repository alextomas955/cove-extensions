using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

// The roles declared here are the only ones that can make an instance download anything. Their verb
// class has no retry entry, so an attempt whose answer did not arrive is reported, not re-issued.

/// <summary>Asks an instance to look for what it monitors and does not hold.</summary>
/// <remarks>Declared by both v2 and v3.</remarks>
public interface IWhisparrSearchGrabbing
{
    /// <summary>Asks the instance to look for the monitored catalogue of entities it holds.</summary>
    /// <remarks>
    /// The two generations spell the command differently: one takes an id array and the other a
    /// single scalar id, and a body carrying the other's shape is accepted and does nothing. Which
    /// shape is sent belongs to the instance the role was obtained from.
    /// <para>
    /// Every id must be one the instance holds. The generation taking an array fails the command
    /// outright on the first id it does not hold.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entityIds"/> is empty.</exception>
    Task<WhisparrResponse> SearchMonitoredAsync(
        WhisparrEntityKind kind, IReadOnlyList<int> entityIds, CancellationToken ct);
}

/// <summary>Asks an instance to look for one catalogue scene it holds.</summary>
/// <remarks>Only v3 declares it: v2 keeps no scene records, so a caller there obtains no role.</remarks>
public interface IWhisparrSceneSearchGrabbing
{
    /// <summary>Asks the instance to look for the scene <paramref name="sceneId"/> names.</summary>
    /// <remarks>
    /// The identifier is the instance's own scene id, not the provider's, and is read off the
    /// instance's row for the scene rather than taken from a browser.
    /// </remarks>
    Task<WhisparrResponse> SearchSceneAsync(int sceneId, CancellationToken ct);
}
