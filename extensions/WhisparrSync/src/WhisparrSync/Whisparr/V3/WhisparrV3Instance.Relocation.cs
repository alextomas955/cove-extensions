using System.Globalization;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Whisparr;

// Moving where the instance records an entity, leaving the entity files where they are.
internal sealed partial class WhisparrV3Instance
{
    // One read, one update, then the re-read of the new folder. The update's body is the resource
    // the read answered and names no transfer parameter, which is what leaves the files where they
    // are. The re-read is not optional: the update rewrites the scene's file record to the old file
    // name under the new folder, which is a path nothing holds, and only the re-read replaces it
    // with what is there.
    //
    // Composed here rather than by the generated client, for the reason the scope change is: the
    // replacement is the answer itself with two members changed, and the generated resource's fixed
    // member set would drop everything it does not declare.
    public async Task<WhisparrResponse> MoveEntityFolderAsync(
        int entityId, string rootFolderPath, string? entityFolderPath, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolderPath);

        var path = string.Create(CultureInfo.InvariantCulture, $"{ScenePath}/{entityId}");
        var held = await ReadAsync(path, ct).ConfigureAwait(false);
        if (WhisparrTransport.Refused(held))
        {
            return held;
        }

        // The status is read before the body because a refusal carries one too, and this
        // generation's is a JSON object. Composed from it, the update would write a member set
        // named by whatever the instance refused with.
        if (MonitoringProjector.AsObject(held.Body) is not { } scene
            || V3BodyProjector.MovedSceneFolder(scene, rootFolderPath, entityFolderPath)
                is not { } body)
        {
            // A success carrying nothing the move could be composed from arrives here. Handed back
            // unchanged it would report a move that happened while the scene is still registered
            // where none of its files sit.
            return held with { Refusal = MonitorRefusalKind.InstanceRefused };
        }

        var moved = await ActAsync(HttpMethod.Put, path, body, ct).ConfigureAwait(false);
        if (WhisparrTransport.Refused(moved))
        {
            return moved;
        }

        var read = await GeneratedCommandAsync(V3BodyProjector.RescanScene(entityId), ct)
            .ConfigureAwait(false);

        return WhisparrTransport.Refused(read) ? read : moved;
    }
}
