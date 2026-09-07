using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

// The roles declared here are the only ones in this product that can make an instance download
// anything, and they share this file for that reason: a call site that never obtains one of them by
// name cannot express the request at all, which is a property of the type set rather than of a
// check. Their verb class has no retry entry, so an attempt whose answer did not arrive is reported
// rather than re-issued.

/// <summary>Asks an instance to look for what it monitors and does not hold.</summary>
/// <remarks>
/// Registered on both generations, and separate from the per-scene role beside it because the reach
/// of the two differs: this one names an entity and looks for everything that entity monitors.
/// </remarks>
public interface IWhisparrSearchGrabbing
{
    /// <summary>Asks the instance to look for the monitored catalogue of one entity it holds.</summary>
    /// <remarks>
    /// Names an entity and nothing else. There is no member taking a release, an indexer or a download
    /// client, so what is looked for is whatever that entity is monitoring at the time and the choice
    /// of where from is the instance's own.
    /// <para>
    /// The connected generation is named because both honour this role and neither spells the command
    /// the way the other does: one takes an id array and the other a single scalar id, and a body
    /// carrying the other's shape is accepted and does nothing. It names a lineage rather than a
    /// route, so which body follows from it belongs to the implementation.
    /// </para>
    /// </remarks>
    Task<WhisparrResponse> SearchMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        int entityId,
        CancellationToken ct);
}

/// <summary>Asks an instance to look for one catalogue scene it holds.</summary>
/// <remarks>
/// Only the newer generation registers it: the older one keeps no scene records at all, so a caller
/// obtains no role and states what happens instead rather than reaching a member that would refuse
/// once it was called.
/// </remarks>
public interface IWhisparrSceneSearchGrabbing
{
    /// <summary>Asks the instance to look for the scene <paramref name="sceneId"/> names.</summary>
    /// <remarks>
    /// Names one scene the instance already holds and nothing else. There is no member taking a
    /// release, an indexer or a download client, so the choice of where to look is the instance's
    /// own.
    /// <para>
    /// The identifier is the instance's, not the provider's. A caller reads it off the instance's own
    /// row for the scene, so nothing here can be aimed by an identifier a browser supplied.
    /// </para>
    /// </remarks>
    Task<WhisparrResponse> SearchSceneAsync(
        Uri baseAddress, string apiKey, int sceneId, CancellationToken ct);
}
