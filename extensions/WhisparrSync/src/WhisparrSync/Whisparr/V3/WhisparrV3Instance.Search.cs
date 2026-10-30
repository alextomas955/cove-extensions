using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

// The two members that can make this instance acquire anything.
internal sealed partial class WhisparrV3Instance
{
    // One of the two members that can make this instance acquire, the other being the per-scene
    // search below, and the only ones whose invocation is recorded on its own. Their verb class has
    // no retry entry: a second search is a second download. This generation's command names an id
    // array and carries every id in one, so each entity is searched once.
    public Task<WhisparrResponse> SearchMonitoredAsync(
        WhisparrEntityKind kind, IReadOnlyList<int> entityIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentOutOfRangeException.ThrowIfZero(entityIds.Count);
        WhisparrSyncLog.SearchIssued(log, kind);

        return GeneratedCommandAsync(V3BodyProjector.SearchAllMonitored(kind, entityIds), ct);
    }

    // Recorded as the entity search is, and given no arguments: the scene, the instance and the key
    // are caller-supplied or credentials, and a log sink is durable and readable. Sent once.
    public Task<WhisparrResponse> SearchSceneAsync(int sceneId, CancellationToken ct)
    {
        WhisparrSyncLog.SceneSearchIssued(log);

        return GeneratedCommandAsync(V3BodyProjector.SearchScene(sceneId), ct);
    }
}
