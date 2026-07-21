using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Options;
using WhisparrSync.Push;

namespace WhisparrSync.Monitor;

/// <summary>
/// The host-free orchestration seam for a studio/performer monitor toggle: it ensures the
/// origin tag (attribution), derives the add's root folder from Whisparr's own root list, selects the version
/// adapter, and delegates the add-then-flip / status projection to it. Keeping the tag-ensure + root-derivation
/// + adapter-selection here means the endpoints call ONE method and hold no wire semantics. Constructor-injected
/// with the transport client + the already-loaded options, so it unit-tests against a fake HTTP handler with no
/// host.
/// </summary>
internal sealed class EntityMonitor(WhisparrClient client, WhisparrOptions options, TimeSpan? monitorSettleDelay = null)
{
    // Passed to the V3 adapter's studio create-path verify loop. Null uses the adapter's production
    // settle default; tests pass TimeSpan.Zero to exercise the re-assert logic without real waiting.
    private readonly TimeSpan? _monitorSettleDelay = monitorSettleDelay;

    // The shared origin-tag-ensure + root-folder-resolve concern, single-sourced with the
    // scene service. Constructed per EntityMonitor instance so its tag-id cache lives for one toggle.
    private readonly AddContextResolver _addContext = new(client, options);

    /// <summary>
    /// The Whisparr tag label applied to every Cove-initiated add. Aliases the single source of truth
    /// on <see cref="AddContextResolver.OriginTagLabel"/> so the literal appears in exactly one const;
    /// kept here as the established name existing callers/tests reference.
    /// </summary>
    internal const string OriginTagLabel = AddContextResolver.OriginTagLabel;

    /// <summary>
    /// Turns monitor ON or OFF for a studio/performer. On ON it ensures the origin tag and
    /// resolves the root-folder path before delegating the add-then-flip; on OFF it delegates a bare
    /// unmonitor (no tag/root work, no add, no delete). Returns the adapter's classified result verbatim so a
    /// bad key / unreachable / v2-deferral surfaces to the caller unchanged.
    /// </summary>
    internal async Task<WhisparrResult<EntityMonitorResult>> SetMonitorAsync(
        EntityKind kind, string stashId, bool monitored, MonitorScope scope, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        if (adapter is null || !adapter.SupportsEntityMonitor(kind))
        {
            // Defer a version/kind with no monitorable entity (a v2 performer) BEFORE the root + origin-tag
            // resolve, so a deferred toggle never creates a stray cove-sync tag on the host.
            return WhisparrResult<EntityMonitorResult>.VersionMismatch(options.DetectedVersion);
        }

        var rootFolderPath = string.Empty;
        IReadOnlyList<int> tagIds = [];

        if (monitored)
        {
            // A monitor-add has no owned file to prefix-match (§Pitfall 1), so derive the root from the
            // fallback rule. Only needed for the add leg, so this work is skipped entirely on OFF.
            var rootResult = await _addContext.ResolveFallbackRootAsync(ct);
            if (!rootResult.IsOk)
            {
                return Propagate<string, EntityMonitorResult>(rootResult);
            }

            rootFolderPath = rootResult.Value!;

            // Ensure the origin tag and carry it on the add so the entity is attributable.
            var tagResult = await _addContext.EnsureOriginTagAsync(ct);
            if (!tagResult.IsOk)
            {
                return Propagate<int, EntityMonitorResult>(tagResult);
            }

            tagIds = [tagResult.Value];
        }

        return await adapter.SetEntityMonitorAsync(
            options.BaseUrl, options.ApiKey, kind, stashId, monitored, scope,
            rootFolderPath, options.QualityProfileId, tagIds, ct);
    }

    /// <summary>
    /// Projects the quiet-status for a studio/performer by delegating to the version adapter (which
    /// derives the counts from its already-fetched Whisparr movie set — no StashDB call).
    /// </summary>
    internal Task<WhisparrResult<EntityStatus>> GetStatusAsync(EntityKind kind, string stashId, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        return adapter is null
            ? Task.FromResult(WhisparrResult<EntityStatus>.VersionMismatch(options.DetectedVersion))
            : adapter.GetEntityStatusAsync(options.BaseUrl, options.ApiKey, kind, stashId, ct);
    }

    // The version adapter for the connected instance, or null when the version is unmanageable (deferred
    // BEFORE the root + origin-tag resolve, so an unknown version never issues a wasted round-trip). A v2
    // studio reaches the real SITE add-then-flip; a v2 performer defers inside the adapter (no v2 performer
    // entity) with no wire call. Mirrors SceneActions.SelectAdapter.
    private IWhisparrAdapter? SelectAdapter()
        => AdapterSelector.SelectForVersion(options.SelectedVersion, client, _monitorSettleDelay);

    // Re-shape a non-Ok result of one payload type into the same state for the monitor return type.
    private static WhisparrResult<TTo> Propagate<TFrom, TTo>(WhisparrResult<TFrom> source)
        => WhisparrResult<TTo>.PropagateFrom(source);
}
