using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Safety;

/// <summary>One Whisparr root folder as this seam projects it: the configured path and whether Whisparr can
/// currently reach it.</summary>
/// <remarks>
/// <see cref="Accessible"/> is carried because the file-less add path picks the first reachable root when an
/// instance has several; dropping it would silently route an add to an unmounted volume.
/// </remarks>
internal sealed record WhisparrRootEntry(string Path, bool Accessible);

/// <summary>
/// The outcome of one root read: the projected roots on success, or an empty set plus the classified
/// <see cref="Reason"/> naming why nothing could be read.
/// </summary>
/// <remarks>
/// <see cref="FailureState"/> preserves the transport classification behind a <c>readFailed</c> reason so a
/// caller that must surface a typed Whisparr failure can propagate it rather than re-deriving one. It is
/// server-side only and is never projected onto the wire — the wire carries the reason literal alone, which
/// holds no host, key or upstream message.
/// </remarks>
internal sealed record WhisparrRootsResult(
    IReadOnlyList<WhisparrRootEntry> Roots, string? Reason, WhisparrResultState? FailureState)
{
    internal static WhisparrRootsResult Available(IReadOnlyList<WhisparrRootEntry> roots) => new(roots, null, null);

    internal static WhisparrRootsResult NotConfigured() => new([], FolderOverlapReason.NotConfigured, null);

    internal static WhisparrRootsResult UnsupportedVersion() => new([], FolderOverlapReason.UnsupportedVersion, null);

    internal static WhisparrRootsResult ReadFailed(WhisparrResultState state)
        => new([], FolderOverlapReason.ReadFailed, state);

    /// <summary>The bare paths, in Whisparr's own order — the shape the fail-closed ingest guard consults.</summary>
    internal IReadOnlyList<string> Paths => [.. Roots.Select(r => r.Path)];
}

/// <summary>
/// The one read path for Whisparr's configured root folders. Every caller that needs roots — the webhook
/// ingest guard, the folder-overlap advisory, and the add-path root derivation — goes through here.
/// </summary>
/// <remarks>
/// What this seam hides is POLICY, not transport: the short cache and its refill gate, the fail-closed rule, the
/// blank-path filter, and the classification of a read that could not happen. It carries NO per-generation
/// branch, and deliberately so — <c>ListRootFoldersAsync</c> is declared once on <see cref="IWhisparrConnection"/>
/// and implemented once on <see cref="WhisparrAdapterBase"/>, both generations answer the same endpoint
/// identically, so there is nothing to switch on. The single version-dependent fact is whether the persisted
/// version is manageable at all, which is a refusal rather than a branch.
/// <para>
/// Credentials arrive through a resolver delegate and <see cref="ReadAsync"/> takes no host or key, so a
/// caller-supplied host is unrepresentable in this type's signature — the SSRF / key-exfiltration control is
/// structural rather than a convention.
/// </para>
/// </remarks>
internal sealed class WhisparrRootsPort(
    Func<CancellationToken, Task<(WhisparrOptions Options, string BaseUrl, string ApiKey)>> resolveStoredCredentials,
    TimeSpan cacheTtl)
{
    // A short in-memory cache of the Whisparr root folders (they change rarely): the webhook ingest guard
    // consults this per event, so it must never issue an uncached GET per event. Fail-closed — a failed fetch
    // leaves the cache untouched and returns no roots, so the containment guard rejects until roots are known.
    private IReadOnlyList<WhisparrRootEntry>? _cachedRoots;
    private DateTime _cachedAtUtc;
    // Static (process-lifetime, never disposed): the extension instance is a long-lived host singleton, and a
    // static gate avoids owning a disposable instance field (CA1001) while still serializing the cache refill.
    private static readonly SemaphoreSlim RefillGate = new(1, 1);

    internal async ValueTask<WhisparrRootsResult> ReadAsync(WhisparrClient client, CancellationToken ct)
    {
        if (_cachedRoots is { } fresh && DateTime.UtcNow - _cachedAtUtc < cacheTtl)
        {
            return WhisparrRootsResult.Available(fresh);
        }

        // A port configured with no cache lifetime holds nothing for the gate to protect, so it must not queue
        // behind a process-wide semaphore that a cached sibling port is holding.
        if (cacheTtl <= TimeSpan.Zero)
        {
            return await RefillAsync(client, ct);
        }

        await RefillGate.WaitAsync(ct);
        try
        {
            // The second freshness test is the point of the gate, not a redundancy: without it every waiter that
            // queued behind a refill still issues its own read, which is exactly the per-event GET the cache exists
            // to prevent.
            if (_cachedRoots is { } cached && DateTime.UtcNow - _cachedAtUtc < cacheTtl)
            {
                return WhisparrRootsResult.Available(cached);
            }

            return await RefillAsync(client, ct);
        }
        finally
        {
            RefillGate.Release();
        }
    }

    private async ValueTask<WhisparrRootsResult> RefillAsync(WhisparrClient client, CancellationToken ct)
    {
        // Stored creds only: the root fetch reuses the saved host/key, never a caller-supplied host.
        var (options, storedHost, storedKey) = await resolveStoredCredentials(ct);
        if (string.IsNullOrWhiteSpace(storedHost))
        {
            return WhisparrRootsResult.NotConfigured();
        }

        // A persisted version this build cannot manage is a refusal BEFORE the transport: the selector returns
        // null rather than guessing an adapter, and the role is then reached through its return value.
        if (AdapterSelector.SelectForVersion(options.SelectedVersion, client) is not IWhisparrConnection connection)
        {
            return WhisparrRootsResult.UnsupportedVersion();
        }

        var result = await connection.ListRootFoldersAsync(storedHost, storedKey, ct);
        if (!result.IsOk || result.Value is not { } rows)
        {
            // fail-closed: no roots → the guard rejects; cache untouched so the next event retries
            return WhisparrRootsResult.ReadFailed(result.State);
        }

        // Whisparr's own order is preserved and nothing is deduped: the add-path root derivation is
        // first-match-wins over this list, so a sort would silently change which root an add lands in.
        var roots = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .Select(r => new WhisparrRootEntry(r.Path!, r.Accessible))
            .ToArray();
        _cachedRoots = roots;
        _cachedAtUtc = DateTime.UtcNow;
        return WhisparrRootsResult.Available(roots);
    }
}
