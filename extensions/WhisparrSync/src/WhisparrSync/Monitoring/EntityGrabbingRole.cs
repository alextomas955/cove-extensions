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
    /// </remarks>
    Task<WhisparrResponse> SearchMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        int entityId,
        CancellationToken ct);
}
