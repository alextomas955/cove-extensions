using System.Runtime.CompilerServices;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Options;

using EndpointMatchGuard = WhisparrSync.Import.EndpointMatchGuard;
using IdentityEndpoint = WhisparrSync.Import.IdentityEndpoint;

namespace WhisparrSync.Monitoring;

/// <inheritdoc cref="IEntitySceneIdentityPort"/>
/// <remarks>
/// Binds the base <see cref="DbContext"/> for the reason its two siblings do: this extension
/// compiles against the host's entity assembly but not against the assembly its context lives in,
/// and the host registers that context resolvable as the base type.
/// <para>
/// The de-duplication and the ordering are the DATABASE's. A set assembled here would cost one
/// loaded row per identified scene, so on a library of millions it would answer correctly and be
/// unusable. Nothing in this file may collect.
/// </para>
/// <para>
/// Two spellings of ONE source therefore yield one identifier twice: the pair is distinct in the
/// database and the same-source rule is the host's, which no provider can translate into a query.
/// The instance answers the second offer as a scene it already holds, which costs one request and
/// no change, and that is preferred to a seen-set that grows with the entity.
/// </para>
/// <para>
/// The endpoint rule is the host's own and is applied in memory for the same reason. Comparing the
/// two spellings as strings would answer that a video the host itself treats as identified carries
/// no identity, and its scene would then be offered to nothing.
/// </para>
/// </remarks>
internal sealed class EntitySceneIdentityPort(DbContext db, OptionsStore options)
    : IEntitySceneIdentityPort
{
    public async IAsyncEnumerable<string> SceneIdentitiesFor(
        WhisparrEntityKind kind,
        int coveId,
        WhisparrGeneration generation,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // The kind is read before the id is, so an unexpressible kind is a fault rather than an
        // entity naming no scene: the two answers mean different things and only one is about the
        // library.
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

    /// <summary>The identity rows one entity's own scenes carry, as a query.</summary>
    /// <remarks>
    /// A studio's scenes reach it through the column its videos carry; a performer's reach it
    /// through the join table, which no studio row appears in.
    /// </remarks>
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
