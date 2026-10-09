using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Contracts;
using WhisparrSync.Identity;
using WhisparrSync.Options;

namespace WhisparrSync.Library;

/// <summary>One card's Cove id beside the identifier the connected instance is given for it.</summary>
public readonly record struct LibraryCardIdentity(int CoveId, string RemoteId);

/// <summary>What one scene is known by on the connected instance, or why it is known by nothing.</summary>
/// <remarks>
/// A scene the library names no identifier for and a scene it names several different ones for are
/// held apart, because they send a reader to different places: one link is missing, the other is a
/// link on the scene's own page that does not belong there.
/// </remarks>
public readonly record struct SceneCardIdentity(string? RemoteId, SceneRefusalKind Refusal)
{
    /// <summary>The library names no identifier for the scene in the namespace asked about.</summary>
    public static SceneCardIdentity Unmatched { get; } =
        new(null, SceneRefusalKind.NoIdentityInThisNamespace);

    /// <summary>
    /// The library names several different identifiers for the scene in that namespace.
    /// </summary>
    /// <remarks>
    /// Answered instead of one of them, because which scene an outbound request would name would
    /// depend on which row was read first.
    /// </remarks>
    public static SceneCardIdentity Ambiguous { get; } =
        new(null, SceneRefusalKind.SeveralIdentitiesInThisNamespace);

    /// <summary>The scene is named by <paramref name="remoteId"/>.</summary>
    public static SceneCardIdentity At(string remoteId)
        => new(remoteId, SceneRefusalKind.None);
}

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

    /// <summary>
    /// What the one scene <paramref name="coveId"/> names is known by in
    /// <paramref name="generation"/>'s namespace, or why it is known by nothing.
    /// </summary>
    /// <remarks>
    /// Answered for a caller acting on one scene, which owes the reader the reason. A page read
    /// takes <see cref="ResolveAsync"/> instead, where an unresolved card is simply absent.
    /// </remarks>
    Task<SceneCardIdentity> ResolveOneAsync(
        int coveId, WhisparrGeneration generation, CancellationToken ct);
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

        var named = await NamedIn(wanted, generation, ct).ConfigureAwait(false);
        var matched = named
            .Where(rows => rows.Value.Count == 1)
            .ToDictionary(rows => rows.Key, rows => rows.Value[0]);

        return [.. coveIds
            .Distinct()
            .Where(matched.ContainsKey)
            .Select(coveId => new LibraryCardIdentity(coveId, matched[coveId]))];
    }

    public async Task<SceneCardIdentity> ResolveOneAsync(
        int coveId, WhisparrGeneration generation, CancellationToken ct)
    {
        if (coveId < 1)
        {
            return SceneCardIdentity.Unmatched;
        }

        var named = await NamedIn([coveId], generation, ct).ConfigureAwait(false);
        if (!named.TryGetValue(coveId, out var carried) || carried.Count == 0)
        {
            return SceneCardIdentity.Unmatched;
        }

        return carried.Count == 1
            ? SceneCardIdentity.At(carried[0])
            : SceneCardIdentity.Ambiguous;
    }

    // The distinct identifiers each video carries in the namespace, so a caller can tell a video
    // named by nothing from one named by several.
    private async Task<Dictionary<int, IReadOnlyList<string>>> NamedIn(
        IReadOnlyList<int> wanted, WhisparrGeneration generation, CancellationToken ct)
    {
        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        var namespaced = IdentityEndpoint.PreferredFor(generation, stored.MetadataProviderEndpoints);

        var carried = await db.Set<VideoRemoteId>()
            .AsNoTracking()
            .Where(row => wanted.Contains(row.VideoId))
            .Select(row => new { row.VideoId, row.Endpoint, row.RemoteId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return carried
            .Where(row => EndpointMatchGuard.SameSource(row.Endpoint, namespaced)
                && !string.IsNullOrWhiteSpace(row.RemoteId))
            .GroupBy(row => row.VideoId)
            .ToDictionary(
                rows => rows.Key,
                IReadOnlyList<string> (rows) =>
                    [.. rows.Select(row => row.RemoteId).Distinct(StringComparer.Ordinal)]);
    }
}
