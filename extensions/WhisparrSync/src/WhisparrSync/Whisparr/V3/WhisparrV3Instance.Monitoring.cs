using System.Globalization;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using V3Api = Whisparr3.Net.Api;

namespace WhisparrSync.Whisparr;

// What this instance is asked to want, and what it answers about an entity it already holds.
internal sealed partial class WhisparrV3Instance
{
    public Task<WhisparrResponse> ReadStudioAsync(string foreignId, CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V3Api.IStudioApi>().GetStudioByIdAsync(Named(foreignId), ct));

    public Task<WhisparrResponse> AddMonitoredStudioAsync(
        string foreignId, MonitorScope scope, AddDefaults defaults, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V3Api.IStudioApi>().PostStudioAsync(
                V3BodyProjector.AddStudio(foreignId, scope, defaults, DateTimeOffset.UtcNow), ct));

    public Task<WhisparrResponse> SetStudioMonitoredAsync(
        int entityId, bool monitored, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V3Api.IStudioEditorApi>().PutStudioEditorAsync(
                V3BodyProjector.SetStudioMonitored(entityId, monitored), ct));

    // A field-scoped patch carrying only what changes: a whole-resource replace would write back a
    // resource read a moment earlier, dropping whatever the read did not answer with.
    public Task<WhisparrResponse> SetSceneMonitoredAsync(
        int sceneId, bool monitored, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sceneId, 1);

        return GeneratedActAsync(
            api => api.Api<V3Api.IMovieApi>().PatchMovieByIdAsync(
                sceneId, V3BodyProjector.SceneMonitorPatch(monitored), ct));
    }

    // Gates only what a later catalogue read adds, which is what this generation's date field
    // expresses.
    //
    // Read then replaced, because the editor resource declares no add-time date gate: a scope sent
    // there is accepted and applies nothing. The read is idempotent and the replace is sent once.
    //
    // Composed here rather than by the generated client, which carries a fixed member set: the
    // replacement is the answer itself with two members changed, and a member the generated
    // resource does not declare would be dropped on the way back out.
    public async Task<WhisparrResponse> SetStudioScopeAsync(
        int entityId, MonitorScope scope, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);

        var path = string.Create(CultureInfo.InvariantCulture, $"{StudioPath}/{entityId}");
        var held = await ReadAsync(path, ct).ConfigureAwait(false);
        if (MonitoringProjector.AsObject(held.Body) is not { } studio)
        {
            return held;
        }

        return await ActAsync(
            HttpMethod.Put, path, V3BodyProjector.WithScope(studio, scope, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
    }

    public Task<WhisparrResponse> ReadPerformerAsync(string foreignId, CancellationToken ct)
        => GeneratedReadAsync(
            api => api.Api<V3Api.IPerformerApi>().GetPerformerByIdAsync(Named(foreignId), ct));

    public Task<WhisparrResponse> AddMonitoredPerformerAsync(
        string foreignId, AddDefaults defaults, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V3Api.IPerformerApi>().PostPerformerAsync(
                V3BodyProjector.AddPerformer(foreignId, defaults), ct));

    public Task<WhisparrResponse> SetPerformerMonitoredAsync(
        int entityId, bool monitored, CancellationToken ct)
        => GeneratedActAsync(
            api => api.Api<V3Api.IPerformerEditorApi>().PutPerformerEditorAsync(
                V3BodyProjector.SetPerformerMonitored(entityId, monitored), ct));

    // The operation is chosen here from the entity kind, so no caller can aim the stored credential
    // at a route of its own naming.
    public Task<WhisparrResponse> ReadEntityPresenceAsync(
        WhisparrEntityKind kind, string foreignId, CancellationToken ct)
        => kind switch
        {
            WhisparrEntityKind.Studio => GeneratedReadAsync(
                api => api.Api<V3Api.IStudioApi>().GetStudioByIdAsync(Named(foreignId), ct)),
            WhisparrEntityKind.Performer => GeneratedReadAsync(
                api => api.Api<V3Api.IPerformerApi>().GetPerformerByIdAsync(Named(foreignId), ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}
