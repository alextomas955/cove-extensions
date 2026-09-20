using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Identity;
using WhisparrSync.Options;

namespace WhisparrSync.Library;

/// <summary>One card's Cove id beside the identifier the connected instance is given for it.</summary>
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
    /// carried with a blank, so a caller has nothing to send for it.
    /// </remarks>
    Task<IReadOnlyList<LibraryCardIdentity>> ResolveAsync(
        IReadOnlyList<int> coveIds, WhisparrGeneration generation, CancellationToken ct);
}

// Binds the base DbContext: this extension compiles against the host's entity assembly but not
// against the assembly its context lives in, and the host registers the context as the base type.
// The query is filtered to the ids the caller asked about, so what it materializes is bounded by one
// rendered page whatever the library holds.
// The host's endpoint rule is applied in memory. Comparing the two spellings as strings would answer
// that a video the host treats as identified carries no identity.
// A video whose matching rows name different scenes is answered for by nothing, because which one an
// outbound request would name depends on row order.
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
