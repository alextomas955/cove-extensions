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
// Every read is a query narrowed on the indexed identifier column, never a navigation walk: a
// filter applied after loading is linear in the library.
internal sealed class EntityIdentityPort(DbContext db, OptionsStore options) : IEntityIdentityPort
{
    public async Task<IdentityResolution> ResolveAsync(
        WhisparrEntityKind kind, int coveId, WhisparrGeneration generation, CancellationToken ct)
    {
        var rows = CarriedBy(kind, coveId);
        if (coveId < 1)
        {
            return IdentityResolution.Unmatched;
        }

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var namespaced = IdentityEndpoint.PreferredFor(generation, stored.MetadataProviderEndpoints);

        var carried = await rows.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        // The endpoint rule is the host's and no provider can translate it into a query, so it is
        // applied in memory. Comparing the two spellings as strings would report an entity the host
        // treats as identified as carrying none. Distinct values rather than matching rows: two
        // spellings of one source carrying the same identifier are no ambiguity.
        var named = carried
            .Where(row => EndpointMatchGuard.SameSource(row.Endpoint, namespaced)
                && !string.IsNullOrWhiteSpace(row.RemoteId))
            .Select(row => row.RemoteId)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToList();

        return named.Count switch
        {
            0 => IdentityResolution.Unmatched,
            1 => IdentityResolution.At(named[0]),
            _ => IdentityResolution.Ambiguous,
        };
    }

    // Each kind has its own identity table and indexed column; no table is reachable from another
    // kind's entity without a navigation walk.
    private IQueryable<CarriedIdentity> CarriedBy(WhisparrEntityKind kind, int coveId)
        => kind switch
        {
            WhisparrEntityKind.Studio => db.Set<StudioRemoteId>()
                .Where(row => row.StudioId == coveId)
                .Select(row => new CarriedIdentity(row.Endpoint, row.RemoteId)),
            WhisparrEntityKind.Performer => db.Set<PerformerRemoteId>()
                .Where(row => row.PerformerId == coveId)
                .Select(row => new CarriedIdentity(row.Endpoint, row.RemoteId)),
            WhisparrEntityKind.Tag => db.Set<TagRemoteId>()
                .Where(row => row.TagId == coveId)
                .Select(row => new CarriedIdentity(row.Endpoint, row.RemoteId)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "This is not an entity kind this product expresses."),
        };

    private sealed record CarriedIdentity(string Endpoint, string RemoteId);
}
