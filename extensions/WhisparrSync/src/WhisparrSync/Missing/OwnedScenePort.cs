using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Identity;

namespace WhisparrSync.Missing;

/// <summary>Which of one page's scenes the library already holds.</summary>
/// <remarks>
/// Bounded by the page: one query whose parameters are the page's own identifiers, whatever the
/// library's size.
/// </remarks>
public interface IOwnedScenePort
{
    /// <summary>
    /// Which of <paramref name="providerSceneIds"/> the library holds under
    /// <paramref name="identityEndpoint"/>.
    /// </summary>
    /// <remarks>
    /// Judged on the connected provider's endpoint alone. A scene the library holds under the other
    /// provider's identifier reads as missing, and there is no bridge between the two.
    /// </remarks>
    Task<IReadOnlySet<string>> ReadOwnedAsync(
        string identityEndpoint, IReadOnlyList<string> providerSceneIds, CancellationToken ct);
}

// Binds the base DbContext rather than the host's own context type: this extension compiles against
// the host's entity assembly but not against the assembly that type lives in, and the host
// registers its context resolvable as the base type.
//
// The query is narrowed on the page's own identifiers, so what one page costs does not grow with
// what the library holds. There is no row cap: a cap truncates with no error.
internal sealed class OwnedScenePort(DbContext db) : IOwnedScenePort
{
    public async Task<IReadOnlySet<string>> ReadOwnedAsync(
        string identityEndpoint, IReadOnlyList<string> providerSceneIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providerSceneIds);

        if (providerSceneIds.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var pageIds = providerSceneIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (pageIds.Length == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var carried = await db.Set<VideoRemoteId>()
            .AsNoTracking()
            .Where(row => pageIds.Contains(row.RemoteId))
            .Select(row => new { row.Endpoint, row.RemoteId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // The endpoint rule is the host's, which no provider can translate, so it is applied in
        // memory. Comparing the two spellings as strings would answer that a scene the host itself
        // treats as identified is not held.
        return carried
            .Where(row => EndpointMatchGuard.SameSource(row.Endpoint, identityEndpoint))
            .Select(row => row.RemoteId)
            .ToHashSet(StringComparer.Ordinal);
    }
}
