using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Whisparr;

// The one member that can make this instance acquire anything.
internal sealed partial class WhisparrV2Instance
{
    // The one member of this instance that can make it acquire anything, and the only one whose
    // invocation is recorded on its own. Its verb class has no retry entry: a second search is a
    // second download.
    //
    // This generation's command names a single scalar id, so it is one command per entity and the
    // first answer that was not accepted is the one reported. Either way each entity is searched
    // once.
    public async Task<WhisparrResponse> SearchMonitoredAsync(
        WhisparrEntityKind kind, IReadOnlyList<int> entityIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentOutOfRangeException.ThrowIfZero(entityIds.Count);
        WhisparrSyncLog.SearchIssued(log, kind);

        WhisparrResponse? answered = null;
        foreach (var entityId in entityIds)
        {
            answered = await GeneratedGrabCommandAsync(V2BodyProjector.SearchMonitored(entityId), ct)
                .ConfigureAwait(false);
            if (MonitoringProjector.Accepted(answered) != MonitorRefusalKind.None)
            {
                return answered;
            }
        }

        return answered!;
    }
}
