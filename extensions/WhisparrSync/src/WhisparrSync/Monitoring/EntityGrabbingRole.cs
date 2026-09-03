using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

/// <summary>Asks an instance to look for what it monitors and does not hold.</summary>
/// <remarks>
/// The only member in this product that can make an instance download anything, and it is alone in
/// this file for that reason: a call site that never obtains this role by name cannot express the
/// request at all, which is a property of the type set rather than of a check.
/// <para>
/// Its verb class has no retry entry, so an attempt whose answer did not arrive is reported rather
/// than re-issued.
/// </para>
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
