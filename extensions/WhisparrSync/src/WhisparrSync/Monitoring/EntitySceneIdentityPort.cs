using System.Runtime.CompilerServices;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Monitoring;

/// <inheritdoc cref="IEntitySceneIdentityPort"/>
internal sealed class EntitySceneIdentityPort(DbContext db, OptionsStore options)
    : IEntitySceneIdentityPort
{
    public async IAsyncEnumerable<string> SceneIdentitiesFor(
        WhisparrEntityKind kind,
        int coveId,
        WhisparrGeneration generation,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var rows = CarriedBy(kind, coveId);
        if (coveId < 1)
        {
            yield break;
        }

        _ = await options.LoadAsync(ct).ConfigureAwait(false);

        var carried = rows
            .AsNoTracking()
            .Select(row => row.RemoteId)
            .OrderBy(remoteId => remoteId)
            .AsAsyncEnumerable();

        await foreach (var remoteId in carried.WithCancellation(ct).ConfigureAwait(false))
        {
            yield return remoteId;
        }
    }

    private IQueryable<VideoRemoteId> CarriedBy(WhisparrEntityKind kind, int coveId)
        => kind switch
        {
            WhisparrEntityKind.Studio => db.Set<VideoRemoteId>()
                .Where(row => row.Video!.StudioId == coveId),
            WhisparrEntityKind.Performer => db.Set<VideoRemoteId>()
                .Where(row => db.Set<VideoPerformer>()
                    .Any(linked => linked.PerformerId == coveId && linked.VideoId == row.VideoId)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This is not an entity kind this product expresses."),
        };
}
