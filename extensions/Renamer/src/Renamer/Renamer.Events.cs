using Cove.Extensions.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer;

/// <summary>
/// The optional auto-renamer event hook. Reacts to the host's fire-and-forget
/// <c>video.updated</c>/<c>image.updated</c> events and re-renames the touched item through
/// the planner+executor — a THIN adapter, no renamer logic lives here.
///
/// audio/text updates are intentionally NOT handled here: the auto-renamer product scope is
/// video/image; audio is still reachable via the manual job/API surface, just not the
/// hook.
///
/// SAFETY: the executor's save re-raises <c>video.updated</c>, which re-enters this
/// handler — an unconditional execute would loop forever. The guard is idempotency: build the plan
/// for the single touched id and short-circuit BEFORE the executor when every item is a non-acting
/// status (no save → no re-raised event → loop broken). Combined with the opt-in default-OFF flag,
/// a real metadata change triggers at most one renamer.
/// </summary>
public sealed partial class Renamer
{
    /// <summary>
    /// The entities whose next update event is this handler's own save coming back.
    /// </summary>
    /// <remarks>
    /// The idempotency guard above breaks the loop only where the plan CONVERGES: rename, re-enter,
    /// find nothing left to do, stop. A pair of names that map to each other never converges, so each
    /// pass acts, saves, and re-raises - and because one entity can hold several files, each pass can
    /// raise more events than the one that started it. That is growth, not a loop, and no per-pass
    /// check can see it.
    /// <para>
    /// Keyed by entity rather than by file because the host raises its update event per ENTITY, so the
    /// file that moved is not recoverable from the event. An entry is claimed before the executor runs
    /// and consumed by the first event that follows, so a genuine edit arriving later is never
    /// swallowed: the worst case is one skipped rename on an entity the user edited in the same instant
    /// its own save came back, and the next edit renames it.
    /// </para>
    /// </remarks>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(RenamerFileKind Kind, int EntityId), byte> _selfSaved = new();

    /// <summary>
    /// Registered by the base ctor (runs before <c>InitializeAsync</c> captures the seams), so this
    /// only wires the routing — the handler bodies, which run later, are what touch the scope/store.
    /// </summary>
    protected override void DefineEventHandlers()
    {
        OnUpdated("video", (evt, ct) => AutoRenamerAsync(RenamerFileKind.Video, evt.EntityId, ct));
        OnUpdated("image", (evt, ct) => AutoRenamerAsync(RenamerFileKind.Image, evt.EntityId, ct));
    }

    /// <summary>
    /// Re-renames a single updated entity when the opt-in flag is set and the item is not already
    /// correctly named. Returns without any DB work when the hook is off; returns without calling
    /// the executor (zero saves) when the plan is entirely non-acting (the re-entrancy guard).
    /// <para>
    /// The whole body is wrapped so that a failure on one updated item (a transient DB error, a
    /// missing folder, etc.) is contained instead of escaping back to the host. The host dispatches
    /// these events fire-and-forget and only logs an escaped exception generically ("Error
    /// dispatching event video.updated") with no clue which item failed. Auto-renamer is an opt-in
    /// convenience, not a correctness guarantee, so the policy here is deliberate: record the failure
    /// with the entity context (kind + id) and stop — do NOT rethrow. One bad item must not turn
    /// every future update into an opaque host-log error or abort the host's dispatch loop. The
    /// manual job/API path remains the authoritative, error-reporting way to renamer.
    /// </para>
    /// </summary>
    private async Task AutoRenamerAsync(RenamerFileKind kind, int entityId, CancellationToken ct)
    {
        var selfSaveKey = (kind, entityId);
        if (_selfSaved.TryRemove(selfSaveKey, out _))
        {
            // This event is the save this handler just made. Stop here rather than at the plan, which
            // cannot tell the two apart and would act again on a template whose output does not settle.
            return;
        }

        try
        {
            var options = await new OptionsStore(Store, _log).LoadAsync(ct);
            if (!options.AutoRenamerOnUpdate)
            {
                return; // opt-in, default off — do zero DB work when disabled.
            }

            // One elevated scope for the whole handler, obtained from the seam that elevates as it
            // creates. The hook carries whichever principal made the edit, or none, and a scope running
            // half its work as System and half as the caller is the kind of split that only shows up as
            // an empty result much later.
            await RunAsSystem.RunInSystemScopeAsync(ScopeFactory, async services =>
            {
                var db = services.GetRequiredService<DbContext>();

                var port = new CoveRenamerDataPort(db, _coveConfig);
                // Route auto-renamer IDENTICALLY to the manual batch and to /preview. Build the same
                // RouteLookups from the same RenamerOptions and use the routing overload, so a matched
                // studio/tag/path rule relocates the just-edited item to its configured destination — the
                // same on-disk outcome the user previews and the batch executes.
                //
                // This does NOT enable dribble-relocate of the whole library: only the entity just edited
                // is planned, and it lands where preview and the batch would put it - a matched rule's own
                // destination, or the DEFAULT destination for an item no rule matched, which leaves the
                // item in place only while that default names neither a root nor a folder template. Either
                // way the move passes the confinement gate via the routed anchor.
                // Preview, auto-renamer, and batch all resolve destinations identically.
                var lookups = BuildLookups(options);
                var plan = await new RenamerPlanner(port).PlanAsync(kind, entityId, options, lookups, ct);

                // Re-entrancy guard: if nothing would actually move, do NOT touch the executor. No save
                // means no re-raised update event, so the save→event→re-enter loop never starts. Gated
                // items land here as SkipGated (only-organized / require-fields respected) and are
                // likewise skipped.
                int actingFiles = plan.Items.Count(i =>
                    i.Status is RenamerStatus.Renamer or RenamerStatus.Move);
                if (actingFiles == 0)
                {
                    return;
                }

                // Open exactly one batch for this per-edit rename, mirroring the manual batch
                // (RunRenamerBatchAsync): mint a runId and call BeginBatchAsync only now, on the acting
                // path, so nothing-acts opens no batch (an empty batch would shadow a prior replayable one
                // from /undo). The SAME journal instance is handed to the executor so its AppendAsync rows
                // land under this batch. The row cap applies here too — one entity can hold more files than
                // the journal takes.
                var runId = Guid.NewGuid().ToString("N");
                using var journal = new CoveRevertJournal(db);
                await OpenOrSuppressBatchAsync(journal, runId, kind, actingFiles, DateTime.UtcNow, ct);

                // Claimed BEFORE the save that raises the event, never after: the host dispatches
                // fire-and-forget, so the event can re-enter this handler before ExecuteAsync returns.
                _selfSaved[selfSaveKey] = 0;

                var executor = new RenamerExecutor(port, EventBus, journal, runId, new DiskMover());
                // Single-entity hook path (no batch concurrency): no pre-resolved folder map — the executor
                // resolves the destination folder itself, safe because this call is not parallelized.
                var result = await executor.ExecuteAsync(plan, options, ct: ct);

                if (result.Renamed.Count == 0)
                {
                    // Nothing saved, so no event is coming to consume the claim. Released here for the
                    // same reason the catches below release it: a claim nothing consumes is taken by the
                    // user's next genuine edit instead, which mutes the hook for this item until some
                    // other edit arrives.
                    _selfSaved.TryRemove(selfSaveKey, out _);
                }

                foreach (var r in result.Renamed)
                {
                    LogAutoRenamed(kind, entityId, r.Status, r.OldPath, r.NewPath);
                }
                foreach (var f in result.Failed)
                {
                    LogAutoRenamerFailed(kind, entityId, f.OldPath, f.NewPath, f.Reason ?? "no reason given");
                }
            });
        }
        catch (OperationCanceledException)
        {
            // The host is shutting the operation down — let cancellation flow as cancellation,
            // not as a swallowed "failure". Nothing was committed past the executor's own
            // per-item transaction boundary.
            //
            // A claim left behind would be consumed by the user's NEXT edit of this entity, silently
            // skipping a rename they asked for; releasing one the save already raised an event for costs
            // at most one extra idempotent pass.
            _selfSaved.TryRemove(selfSaveKey, out _);
            throw;
        }
#pragma warning disable CA1031 // Host event-dispatch boundary: nothing may escape into the host.
        catch (Exception ex)
        {
            // Contain the failure with enough context to diagnose it, then stop. Auto-renamer is
            // best-effort; the next update (or a manual renamer) gets a fresh attempt. Cancellation
            // never reaches here — the catch above rethrows it.
            _selfSaved.TryRemove(selfSaveKey, out _);
            LogAutoRenamerError(ex, kind, entityId);
        }
#pragma warning restore CA1031
    }
}
