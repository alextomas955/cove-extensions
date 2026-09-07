using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;

namespace WhisparrSync.Missing;

/// <summary>What the library calls one entity.</summary>
/// <param name="Name">The entity's own name.</param>
/// <param name="Aliases">Every alias the library holds for it, which may be none.</param>
public sealed record EntityName(string Name, IReadOnlyList<string> Aliases);

/// <summary>What the library calls one entity, for a provider that matches on a name.</summary>
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

/// <inheritdoc cref="IEntityNamePort"/>
/// <remarks>
/// Binds the base <see cref="DbContext"/> rather than the host's own context type: this extension
/// compiles against the host's entity assembly but not against the assembly that type lives in, and
/// the host registers its context resolvable as the base type.
/// <para>
/// Each kind has its own table and its own alias table, and the aliases are projected in the same
/// query rather than walked through a navigation property afterwards.
/// </para>
/// </remarks>
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

/// <summary>What the provider calls one Cove entity, or that it names none.</summary>
/// <remarks>
/// Three steps in order: the identifier the library already holds for the connected provider's
/// endpoint, then the provider's own exact-name lookup, then no catalogue. The same three for every
/// entity kind.
/// <para>
/// Two exact matches counts as no identifier. Choosing between them would leave the answer to match
/// order, which is not something a caller or a reader chose.
/// </para>
/// <para>
/// The name is read from the library rather than supplied, so what an entity is called has one
/// source. It is read only once the first step has found no identifier, so the ordinary path costs
/// no extra query.
/// </para>
/// </remarks>
internal sealed class MissingIdentityResolver(
    IEntityIdentityPort identities, IProviderCatalogue catalogue, IEntityNamePort names)
{
    /// <summary>
    /// What the provider calls the <paramref name="kind"/> entity <paramref name="coveId"/> names,
    /// or why it names none.
    /// </summary>
    /// <remarks>
    /// The library's own steps always reach an answer, so only the provider's lookup can report
    /// that nothing was reached.
    /// </remarks>
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

        return await catalogue
            .LookUpByNameAsync(kind, named.Name, named.Aliases, ct)
            .ConfigureAwait(false);
    }
}
