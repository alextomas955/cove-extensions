using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Options;

using EndpointMatchGuard = WhisparrSync.Import.EndpointMatchGuard;
using IdentityEndpoint = WhisparrSync.Import.IdentityEndpoint;

namespace WhisparrSync.Monitoring;

/// <inheritdoc cref="IEntityIdentityPort"/>
/// <remarks>
/// Binds the base <see cref="DbContext"/> rather than the host's own context type: this extension
/// compiles against the host's entity assembly but not against the assembly that type lives in, and
/// the host registers its context resolvable as the base type.
/// <para>
/// Every read is a query narrowed on the indexed identifier column, never a walk of a navigation
/// property. A read that reached rows through a tracked entity would depend on what else the scope
/// had already loaded, and a filter applied after loading every row would be linear in the library.
/// </para>
/// <para>
/// The identifier is sent to the instance exactly as the library holds it. A prefixed form looks
/// more correct and fails in silence: the older generation answers a <c>tpdb:</c> prefixed uuid with
/// an empty result and a success status. No prefix is composed anywhere on this path.
/// </para>
/// </remarks>
internal sealed class EntityIdentityPort(DbContext db, OptionsStore options) : IEntityIdentityPort
{
    public async Task<IdentityResolution> ResolveStudioAsync(
        int studioId, WhisparrGeneration generation, CancellationToken ct)
    {
        if (studioId < 1)
        {
            return IdentityResolution.Unmatched;
        }

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var namespaced = IdentityEndpoint.PreferredFor(generation, stored.MetadataProviderEndpoints);

        var carried = await db.Set<StudioRemoteId>()
            .AsNoTracking()
            .Where(row => row.StudioId == studioId)
            .Select(row => new { row.Endpoint, row.RemoteId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // The endpoint rule is the host's, which no provider can translate, so it is applied in
        // memory. Comparing the two spellings as strings would answer that a studio the host itself
        // treats as identified carries no identity.
        var found = carried.Find(row => EndpointMatchGuard.SameSource(row.Endpoint, namespaced));
        return found is null || string.IsNullOrWhiteSpace(found.RemoteId)
            ? IdentityResolution.Unmatched
            : IdentityResolution.At(found.RemoteId);
    }
}
