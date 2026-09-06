using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Providers;

namespace WhisparrSync.Missing;

/// <summary>What the provider calls one Cove entity, or that it names none.</summary>
/// <remarks>
/// Three steps in order: the identifier the library already holds for the connected provider's
/// endpoint, then the provider's own exact-name lookup, then no catalogue.
/// <para>
/// Two exact matches counts as no identifier. Choosing between them would leave the answer to match
/// order, which is not something a caller or a reader chose.
/// </para>
/// </remarks>
internal sealed class MissingIdentityResolver(
    IEntityIdentityPort identities, IProviderCatalogue catalogue)
{
    /// <summary>
    /// What the provider calls the <paramref name="kind"/> entity <paramref name="coveId"/> names, or
    /// null where it names none.
    /// </summary>
    internal async Task<string?> ResolveAsync(
        WhisparrEntityKind kind,
        int coveId,
        WhisparrGeneration generation,
        string? entityName,
        IReadOnlyList<string> aliases,
        CancellationToken ct)
    {
        var carried = await identities
            .ResolveAsync(kind, coveId, generation, ct)
            .ConfigureAwait(false);

        if (carried.Refusal == MonitorRefusalKind.SeveralIdentitiesInThisNamespace)
        {
            return null;
        }

        if (carried.ForeignId is { Length: > 0 } held)
        {
            return held;
        }

        if (string.IsNullOrWhiteSpace(entityName))
        {
            return null;
        }

        var looked = await catalogue
            .LookUpByNameAsync(kind, entityName, aliases, ct)
            .ConfigureAwait(false);

        return looked.IsAmbiguous ? null : looked.ProviderEntityId;
    }
}
