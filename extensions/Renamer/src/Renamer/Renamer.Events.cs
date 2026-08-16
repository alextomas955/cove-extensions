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
        try
        {
            var options = await new OptionsStore(Store).LoadAsync(ct);
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
                // This does NOT enable dribble-relocate of the whole library: only an explicitly-MATCHED
                // routing rule relocates, an UNMATCHED item stays in place (SourceConfine), and the move
                // still passes the allowlist/canonical confinement gate via the routed anchor.
                // Preview, auto-renamer, and batch all resolve destinations identically.
                var lookups = BuildLookups(options);
                var plan = await new RenamerPlanner(port).PlanAsync(kind, entityId, options, lookups, ct);

                // Re-entrancy guard: if nothing would actually move, do NOT touch the executor. No save
                // means no re-raised update event, so the save→event→re-enter loop never starts. Gated
                // items land here as SkipGated (only-organized / require-fields respected) and are
                // likewise skipped.
                int actingFiles = plan.Items.Count(i =>
                    i.Status is RenamerStatus.Rename or RenamerStatus.Move);
                if (actingFiles == 0)
                {
                    return;
                }

                // Open exactly one batch for this per-edit rename, mirroring the manual batch
                // (RunRenamerBatchAsync): mint a runId and call BeginBatchAsync only now, on the acting
                // path, so nothing-acts opens no batch. Opening one here no longer costs the previous
                // batch its rows — each batch is its own set of rows keyed by run id, so a background edit
                // can no longer erase the undo record of a deliberate rename.
                var runId = Guid.NewGuid().ToString("N");
                using var journal = new CoveRevertJournal(db);
                await journal.BeginBatchAsync(runId, kind, DateTime.UtcNow, ct);

                var executor = new RenamerExecutor(port, EventBus, journal, runId, new DiskMover());
                // Single-entity hook path (no batch concurrency): no pre-resolved folder map — the
                // executor resolves the destination folder itself, safe because this call is not
                // parallelized.
                var result = await executor.ExecuteAsync(plan, options, ct: ct);

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
            throw;
        }
        catch (Exception ex)
        {
            // Contain the failure with enough context to diagnose it, then stop. Auto-renamer is
            // best-effort; the next update (or a manual renamer) gets a fresh attempt.
            LogAutoRenamerError(ex, kind, entityId);
        }
    }
}
