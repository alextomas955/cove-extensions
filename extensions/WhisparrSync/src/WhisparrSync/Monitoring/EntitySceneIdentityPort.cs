using System.Runtime.CompilerServices;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Identity;
using WhisparrSync.Options;

namespace WhisparrSync.Monitoring;

// Binds the base DbContext because this extension compiles against the host's entity assembly but
// not against the assembly its context lives in, and the host registers that context resolvable as
// the base type.
//
// The endpoint rule is the host's and no provider can translate it into a query, so it is applied
// in memory over the streamed rows. Two spellings of one source therefore yield the same identifier
// twice, and the second offer costs one request and no change; a seen-set would grow with the
// entity.
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

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var namespaced = IdentityEndpoint.PreferredFor(generation, stored.MetadataProviderEndpoints);

        var carried = rows
            .AsNoTracking()
            .Select(row => new { row.Endpoint, row.RemoteId })
            .Distinct()
            .OrderBy(row => row.Endpoint)
            .ThenBy(row => row.RemoteId)
            .AsAsyncEnumerable();

        await foreach (var row in carried.WithCancellation(ct).ConfigureAwait(false))
        {
            if (EndpointMatchGuard.SameSource(row.Endpoint, namespaced)
                && !string.IsNullOrWhiteSpace(row.RemoteId))
            {
                yield return row.RemoteId;
            }
        }
    }

    // A studio's scenes are reached through the column its videos carry; a performer's through the
    // join table, which holds no studio row.
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
