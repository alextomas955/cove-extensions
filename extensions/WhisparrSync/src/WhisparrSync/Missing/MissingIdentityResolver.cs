using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;

namespace WhisparrSync.Missing;

public sealed record EntityName(string Name, IReadOnlyList<string> Aliases);

public interface IEntityNamePort
{
    /// <summary>
    /// What the library calls the <paramref name="kind"/> entity <paramref name="coveId"/> names, or
    /// null where it holds no such entity.
    /// </summary>
    /// <remarks>
    /// One query narrowed on the entity's own key. Nothing here grows with the library.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a kind this product expresses.
    /// </exception>
    Task<EntityName?> ReadNameAsync(WhisparrEntityKind kind, int coveId, CancellationToken ct);
}

// Binds the base DbContext rather than the host's own context type: this extension compiles against
// the host's entity assembly but not against the assembly that type lives in, and the host
// registers its context resolvable as the base type.
internal sealed class EntityNamePort(DbContext db) : IEntityNamePort
{
    public async Task<EntityName?> ReadNameAsync(
        WhisparrEntityKind kind, int coveId, CancellationToken ct)
        => kind switch
        {
            WhisparrEntityKind.Studio => await db.Set<Studio>()
                .AsNoTracking()
                .Where(row => row.Id == coveId)
                .Select(row => new EntityName(
                    row.Name, row.Aliases.Select(alias => alias.Alias).ToList()))
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false),
            WhisparrEntityKind.Performer => await db.Set<Performer>()
                .AsNoTracking()
                .Where(row => row.Id == coveId)
                .Select(row => new EntityName(
                    row.Name, row.Aliases.Select(alias => alias.Alias).ToList()))
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false),
            WhisparrEntityKind.Tag => await db.Set<Tag>()
                .AsNoTracking()
                .Where(row => row.Id == coveId)
                .Select(row => new EntityName(
                    row.Name, row.Aliases.Select(alias => alias.Alias).ToList()))
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
}

// Two exact name matches counts as no identifier: choosing between them would leave the answer to
// match order.
internal sealed class MissingIdentityResolver(
    IEntityIdentityPort identities, ProviderCatalogueSource catalogues, IEntityNamePort names)
{
    internal async Task<ProviderIdentityLookup> ResolveAsync(
        WhisparrEntityKind kind,
        int coveId,
        WhisparrGeneration generation,
        CancellationToken ct)
    {
        var carried = await identities
            .ResolveAsync(kind, coveId, generation, ct)
            .ConfigureAwait(false);

        if (carried.Refusal == MonitorRefusalKind.SeveralIdentitiesInThisNamespace)
        {
            return ProviderIdentityLookup.Ambiguous;
        }

        if (carried.ForeignId is { Length: > 0 } held)
        {
            return ProviderIdentityLookup.Matched(held);
        }

        var named = await names.ReadNameAsync(kind, coveId, ct).ConfigureAwait(false);
        if (named is not { Name.Length: > 0 })
        {
            return ProviderIdentityLookup.Unmatched;
        }

        var catalogue = await catalogues(ct).ConfigureAwait(false);
        return await catalogue
            .LookUpByNameAsync(kind, named.Name, named.Aliases, ct)
            .ConfigureAwait(false);
    }
}
