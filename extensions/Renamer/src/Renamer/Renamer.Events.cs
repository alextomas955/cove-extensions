using Cove.Extensions.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Planner;

namespace Renamer;

/// <summary>
/// The optional auto-renamer hook: reacts to the host's fire-and-forget <c>video.updated</c> and
/// <c>image.updated</c> events and re-renames the touched item through the planner and executor.
/// </summary>
/// <remarks>
/// The executor's save re-raises <c>video.updated</c>, which re-enters this handler, so an
/// unconditional execute would loop. The plan for the single touched id is built first and the
/// handler stops before the executor when no item acts: no save, no re-raised event. Audio and text
/// updates are not handled here; they stay reachable through the job and API surface.
/// </remarks>
public sealed partial class Renamer
{
    /// <summary>The entities whose next update event is this handler's own save coming back.</summary>
    /// <remarks>
    /// The idempotency guard breaks the loop only where the plan converges. A pair of names that map
    /// to each other never converges, so each pass acts, saves and re-raises, and one entity can hold
    /// several files, so a pass can raise more events than started it. Keyed by entity because the
    /// host raises its event per entity, so the file that moved is not recoverable from the event. An
    /// entry is claimed before the executor runs and consumed by the first event that follows, so the
    /// worst case is one skipped rename on an entity edited in the same instant its own save returned.
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(RenamerFileKind Kind, int EntityId), byte> _selfSaved = new();

    // Registered by the base constructor, before InitializeAsync captures the seams, so this wires
    // only the routing. The handler bodies run later and touch the scope and store.
    protected override void DefineEventHandlers()
    {
        OnUpdated("video", (evt, ct) => AutoRenamerAsync(RenamerFileKind.Video, evt.EntityId, ct));
        OnUpdated("image", (evt, ct) => AutoRenamerAsync(RenamerFileKind.Image, evt.EntityId, ct));
    }

    private async Task AutoRenamerAsync(RenamerFileKind kind, int entityId, CancellationToken ct)
    {
        var selfSaveKey = (kind, entityId);
        if (_selfSaved.TryRemove(selfSaveKey, out _))
        {
            // This event is the save this handler just made. Stopping at the plan would not work: it
            // cannot tell the two apart, and would act again on a template whose output does not settle.
            return;
        }

        try
        {
            var options = await StoredOptions.LoadAsync(ct);
            if (!options.AutoRenamerOnUpdate)
            {
                return;
            }

            // One elevated scope for the whole handler, from the seam that elevates as it creates. The
            // hook carries whichever principal made the edit, or none, and a scope running half its
            // work as System surfaces only as an empty result much later.
            await RunAsSystem.RunInSystemScopeAsync(ScopeFactory, async services =>
            {
                var db = services.GetRequiredService<DbContext>();

                var port = new CoveRenamerDataPort(db, _coveConfig);

                // Preview, auto-renamer and batch resolve destinations identically, so a matched
                // studio, tag or path rule relocates the edited item to its configured destination.
                // Only the edited entity is planned, so this does not relocate the library.
                var lookups = RouteLookups.From(options, LogInvalidRouteRegex);
                var plan = await new RenamerPlanner(port).PlanAsync(kind, entityId, options, lookups, ct);

                // Re-entrancy guard: nothing moves, so the executor is not touched, no save happens,
                // and the save-event-re-enter loop never starts. Gated items land here as SkipGated.
                int actingFiles = plan.Items.Count(i =>
                    i.Status is RenamerStatus.Rename or RenamerStatus.Move);
                if (actingFiles == 0)
                {
                    return;
                }

                // The batch opens only on the acting path: an empty batch would shadow a prior
                // replayable one from /undo. Each edit is its own user action, so each gets its own
                // operation id and its undo reaches that edit alone. The journal instance is shared
                // with the executor so its rows land under this batch.
                var runId = Guid.NewGuid().ToString("N");
                await using var journal = new CoveRevertJournal(db);
                await journal.BeginBatchAsync(runId, runId, kind, DateTime.UtcNow, ct);

                // Claimed before the save that raises the event: the host dispatches fire-and-forget,
                // so the event can re-enter this handler before ExecuteAsync returns.
                _selfSaved[selfSaveKey] = 0;

                var executor = new RenamerExecutor(port, EventBus, journal, runId);

                // No pre-resolved folder map: this call is not parallelized, so the executor resolves
                // the destination folder itself.
                var result = await executor.ExecuteAsync(plan, options, ct: ct);

                if (result.Renamed.Count == 0)
                {
                    // Nothing saved, so no event is coming to consume the claim. A claim nothing
                    // consumes is taken by the user's next genuine edit, muting the hook for this item.
                    _selfSaved.TryRemove(selfSaveKey, out _);
                }

                LogAutoRenamerResult(kind, entityId, result);
            });
        }
        catch (OperationCanceledException)
        {
            // Cancellation flows as cancellation. Releasing a claim the save already raised an event
            // for costs one extra idempotent pass; leaving one behind silently skips the user's next
            // edit of this entity.
            _selfSaved.TryRemove(selfSaveKey, out _);
            throw;
        }
#pragma warning disable CA1031 // Host event-dispatch boundary: nothing may escape into the host.
        catch (Exception ex)
        {
            // The host dispatches fire-and-forget and logs an escaped exception with no clue which
            // item failed. Auto-renamer is an opt-in convenience, so record the entity and stop; the
            // next update or a manual rename gets a fresh attempt.
            _selfSaved.TryRemove(selfSaveKey, out _);
            LogAutoRenamerError(ex, kind, entityId);
        }
#pragma warning restore CA1031
    }

    private void LogAutoRenamerResult(RenamerFileKind kind, int entityId, RenamerExecutor.RenamerRunResult result)
    {
        foreach (var r in result.Renamed)
        {
            LogAutoRenamed(kind, entityId, r.Status, r.OldPath, r.NewPath);
            if (r.Reason is { Length: > 0 } warning)
            {
                LogAutoRenamedWithWarning(kind, entityId, warning);
            }
        }
        foreach (var f in result.Failed)
        {
            LogAutoRenamerFailed(kind, entityId, f.OldPath, f.NewPath, f.Reason ?? "no reason given");
        }
    }
}
