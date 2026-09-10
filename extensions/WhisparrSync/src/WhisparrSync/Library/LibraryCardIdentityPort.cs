using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Identity;
using WhisparrSync.Options;

namespace WhisparrSync.Library;

/// <summary>One card's Cove id beside the identifier the connected instance is given for it.</summary>
/// <param name="CoveId">The card the caller asked about.</param>
/// <param name="RemoteId">The scene identifier the library holds in the connected namespace.</param>
public readonly record struct LibraryCardIdentity(int CoveId, string RemoteId);

/// <summary>Which identifier each scene card on one page is known by.</summary>
public interface ILibraryCardIdentityPort
{
    /// <summary>
    /// The identifier each of <paramref name="coveIds"/> is known by, for the ones that have exactly
    /// one, in the order they were given.
    /// </summary>
    /// <remarks>
    /// A video the library names no single identifier for is absent from the answer rather than
    /// carried with a blank, so a caller has nothing to send for it and cannot state a status.
    /// </remarks>
    Task<IReadOnlyList<LibraryCardIdentity>> ResolveAsync(
        IReadOnlyList<int> coveIds, WhisparrGeneration generation, CancellationToken ct);
}

/// <inheritdoc cref="ILibraryCardIdentityPort"/>
/// <remarks>
/// Binds the base <see cref="DbContext"/> for the reason its siblings do: this extension compiles
/// against the host's entity assembly but not against the assembly its context lives in, and the
/// host registers that context resolvable as the base type.
/// <para>
/// The query is filtered to the ids the caller asked about, so what it materializes is bounded by one
/// rendered page whatever the library holds. Nothing here reads a set assembled from the library.
/// </para>
/// <para>
/// The endpoint rule is the host's own and is applied in memory. Comparing the two spellings as
/// strings would answer that a video the host itself treats as identified carries no identity, and
/// its card would then be silent for a reason that is not about the instance.
/// </para>
/// <para>
/// A video whose matching rows name different scenes is answered for by nothing. Which of them an
/// outbound request would name depends on row order, and row order is not something a reader chose.
/// </para>
/// </remarks>
internal sealed class LibraryCardIdentityPort(DbContext db, OptionsStore options)
    : ILibraryCardIdentityPort
{
    public async Task<IReadOnlyList<LibraryCardIdentity>> ResolveAsync(
        IReadOnlyList<int> coveIds, WhisparrGeneration generation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(coveIds);

        var wanted = coveIds.Where(coveId => coveId >= 1).Distinct().ToArray();
        if (wanted.Length == 0)
        {
            return [];
        }

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var namespaced = IdentityEndpoint.PreferredFor(generation, stored.MetadataProviderEndpoints);

        var carried = await db.Set<VideoRemoteId>()
            .AsNoTracking()
            .Where(row => wanted.Contains(row.VideoId))
            .Select(row => new { row.VideoId, row.Endpoint, row.RemoteId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var matched = carried
            .Where(row => EndpointMatchGuard.SameSource(row.Endpoint, namespaced)
                && !string.IsNullOrWhiteSpace(row.RemoteId))
            .GroupBy(row => row.VideoId)
            .Where(rows => rows.Select(row => row.RemoteId).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(rows => rows.Key, rows => rows.First().RemoteId);

        return [.. coveIds
            .Distinct()
            .Where(matched.ContainsKey)
            .Select(coveId => new LibraryCardIdentity(coveId, matched[coveId]))];
    }
}
