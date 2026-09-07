using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Push;

namespace WhisparrSync.Monitor;

/// <summary>
/// The host-free orchestration seam for a studio/performer monitor toggle: it ensures the
/// origin tag (attribution), derives the add's root folder and quality profile from the connected instance,
/// selects the version adapter, and delegates the add-then-flip / status projection to it. Keeping the
/// tag-ensure + derivations + adapter-selection here means the endpoints call ONE method and hold no wire
/// semantics. Constructor-injected
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
        if (adapter is null || (kind != EntityKind.Studio && adapter is not IWhisparrPerformerMonitor))
        {
            // Defer a version/kind with no monitorable entity (a v2 performer never holds
            // IWhisparrPerformerMonitor) BEFORE the root + origin-tag resolve, so a deferred toggle never creates
            // a stray cove-sync tag on the host.
            return WhisparrResult<EntityMonitorResult>.VersionMismatch(options.DetectedVersion);
        }

        var rootFolderPath = string.Empty;
        int qualityProfileId = 0;
        IReadOnlyList<int> tagIds = [];

        if (monitored)
        {
            // A monitor-add has no owned file to prefix-match, so derive the root from the fallback
            // rule. Only needed for the add leg, so this work is skipped entirely on OFF.
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

            // The entity created here IS the studio/performer, so there is no parent whose profile to inherit —
            // it takes the instance's own. The OFF leg skips this: a flip echoes the existing row's profile.
            var profileResult = await _addContext.ResolveQualityProfileAsync(null, ct);
            if (!profileResult.IsOk)
            {
                return Propagate<int, EntityMonitorResult>(profileResult);
            }

            qualityProfileId = profileResult.Value;
        }

        // Interface-presence dispatch: the aggregate always carries IWhisparrStudioMonitor; the gate above proved
        // a performer target holds IWhisparrPerformerMonitor.
        return kind == EntityKind.Studio
            ? await adapter.SetStudioMonitorAsync(
                options.BaseUrl, options.ApiKey, stashId, monitored, scope, rootFolderPath, qualityProfileId, tagIds, ct)
            : await ((IWhisparrPerformerMonitor)adapter).SetPerformerMonitorAsync(
                options.BaseUrl, options.ApiKey, stashId, monitored, scope, rootFolderPath, qualityProfileId, tagIds, ct);
    }

    /// <summary>
    /// Registers a studio's PRESENCE (a v2/site add) with monitoring OFF and grabbing disarmed. Resolves the
    /// fallback root + ensures the origin tag — the same attribution spine <see cref="SetMonitorAsync"/> uses on
    /// its ON leg — then delegates to <see cref="IWhisparrStudioMonitor.RegisterStudioAsync"/>. Returns the
    /// adapter's classified result verbatim (a v3 no-op surfaces unchanged). Register is a studio/site verb only —
    /// a performer has no registrable v2 entity, so a non-studio kind defers before any wire work.
    /// </summary>
    internal async Task<WhisparrResult<EntityMonitorResult>> RegisterEntityAsync(
        EntityKind kind, string stashId, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        if (adapter is null || kind != EntityKind.Studio)
        {
            return WhisparrResult<EntityMonitorResult>.VersionMismatch(options.DetectedVersion);
        }

        // Loop-safety: register only makes the site PRESENT — it never flips monitoring on nor searches.
        var rootResult = await _addContext.ResolveFallbackRootAsync(ct);
        if (!rootResult.IsOk)
        {
            return Propagate<string, EntityMonitorResult>(rootResult);
        }

        var tagResult = await _addContext.EnsureOriginTagAsync(ct);
        if (!tagResult.IsOk)
        {
            return Propagate<int, EntityMonitorResult>(tagResult);
        }

        var profileResult = await _addContext.ResolveQualityProfileAsync(null, ct);
        if (!profileResult.IsOk)
        {
            return Propagate<int, EntityMonitorResult>(profileResult);
        }

        return await adapter.RegisterStudioAsync(
            options.BaseUrl, options.ApiKey, stashId,
            rootResult.Value!, profileResult.Value, [tagResult.Value], ct);
    }

    /// <summary>
    /// Projects the quiet-status for a studio/performer by delegating to the version adapter (which
    /// derives the counts from its already-fetched Whisparr movie set — no StashDB call).
    /// </summary>
    internal Task<WhisparrResult<EntityStatus>> GetStatusAsync(EntityKind kind, string stashId, CancellationToken ct)
    {
        var adapter = SelectAdapter();
        if (adapter is null)
        {
            return Task.FromResult(WhisparrResult<EntityStatus>.VersionMismatch(options.DetectedVersion));
        }

        return kind == EntityKind.Studio
            ? adapter.GetStudioStatusAsync(options.BaseUrl, options.ApiKey, stashId, ct)
            : adapter is IWhisparrPerformerMonitor performer
                ? performer.GetPerformerStatusAsync(options.BaseUrl, options.ApiKey, stashId, ct)
                : Task.FromResult(WhisparrResult<EntityStatus>.VersionMismatch(options.DetectedVersion));
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
