using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// Resolves <see cref="WhisparrCapabilities"/> for a connection by reading the instance's own API description.
/// </summary>
/// <remarks>
/// <para>
/// The document is untrusted input, and it is trusted for exactly one thing it cannot abuse: a forged or
/// truncated document can only REMOVE a capability, which fails closed to a classified refusal at the caller.
/// There is no shape it can take that widens a read.
/// </para>
/// <para>
/// <paramref name="memoize"/> is the caller-supplied memo so one sync run resolves the answer once rather than
/// once per entity; a port constructed without one reads the document on every call.
/// </para>
/// </remarks>
internal sealed class WhisparrCapabilityPort(
    WhisparrClient client,
    Func<string, Func<Task<WhisparrResult<WhisparrCapabilities>>>, CancellationToken, Task<WhisparrResult<WhisparrCapabilities>>>? memoize = null)
{
    /// <summary>
    /// Resolves the connection's capabilities, or the classified reason the document could not be read.
    /// </summary>
    /// <remarks>
    /// The classification is handed back rather than flattened because a verdict requires a read that
    /// ANSWERED: a document that did not arrive means "unknown", which is not the same fact as a document that
    /// arrived declaring no such route. A caller that needs the role treats both as absent (fail-closed); a
    /// caller that merely gates on the role must not report an unreachable instance as a build missing a
    /// feature.
    /// </remarks>
    internal Task<WhisparrResult<WhisparrCapabilities>> ResolveAsync(
        string baseUrl, string selectedVersion, CancellationToken ct)
        => memoize is null
            ? ReadAsync(baseUrl, ct)
            : memoize(CacheKey(baseUrl, selectedVersion), () => ReadAsync(baseUrl, ct), ct);

    /// <summary>The fail-closed reading of <see cref="ResolveAsync"/> — anything but an answer is absent.</summary>
    internal static WhisparrCapabilities OrAbsent(WhisparrResult<WhisparrCapabilities> read)
        => read.IsOk ? read.Value! : WhisparrCapabilities.None;

    // The connected version LEADS the key for the same reason DiscoveryCacheKeys leads with it: the
    // connection-scoped clear does not see a within-connection version switch on the same URL, so a
    // version-less key would answer a v2 connection with the v3 generation's document.
    private static string CacheKey(string baseUrl, string selectedVersion) => selectedVersion + "\n" + baseUrl;

    private async Task<WhisparrResult<WhisparrCapabilities>> ReadAsync(string baseUrl, CancellationToken ct)
    {
        var document = await client.GetOpenApiDocumentAsync(baseUrl, ct);
        if (!document.IsOk)
        {
            return WhisparrResult<WhisparrCapabilities>.PropagateFrom(document);
        }

        IEnumerable<string> declared = document.Value!.Paths?.Keys ?? Enumerable.Empty<string>();
        return WhisparrResult<WhisparrCapabilities>.Ok(WhisparrCapabilities.From(declared));
    }
}
