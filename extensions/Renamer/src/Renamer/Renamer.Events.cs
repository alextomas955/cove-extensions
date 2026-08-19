using System.Collections.Concurrent;
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
/// handler — an unconditional execute would loop forever. TWO separate guards hold it down, and
/// reading them as redundancy is a mistake: a self-save suppression, which is what breaks the loop, and a
/// plan-is-empty short-circuit, which stops a pass that would act on nothing from opening a batch.
/// Each is stated in full at its own site inside <see cref="AutoRenamerAsync"/>.
/// </summary>
public sealed partial class Renamer
{
    /// <summary>
    /// The entities whose next update event was caused by this handler's OWN executor save, keyed by the
    /// (kind, id) pair the handler is invoked with. Membership is the suppression; the value carries
    /// nothing.
    /// </summary>
    /// <remarks>
    /// In memory only, and deliberately so: the guard reads no database row, consults no timestamp and
    /// no time window, and holds nothing across a process restart — so an interrupted or restarted host
    /// leaves behind no suppression to swallow the first edit after it comes back. Concurrent because
    /// the host dispatches these events fire-and-forget and can have several in flight at once; two
    /// entities cannot interfere with each other because each is its own key.
    /// </remarks>
    private readonly ConcurrentDictionary<(RenamerFileKind Kind, int EntityId), byte> _selfSaved = new();

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
    /// correctly named. Returns without any DB work when the hook is off; returns before planning when
    /// this handler's own save is what raised the event (the re-entrancy suppression); and returns
    /// without calling the executor when the plan is entirely non-acting.
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
                var selfSaveKey = (kind, entityId);

                // THE re-entrancy guard, and the one that actually breaks the loop. The executor's save
                // makes the host re-raise the update event for the very entity just saved, and that
                // re-entry is the loop's engine — so an item this handler itself just saved is not
                // planned at all: no plan, no run id, no batch, no executor call.
                //
                // SINGLE-USE, never a mode. One save re-raises one event, so the token is CONSUMED here
                // and the item is live again immediately. A suppression that stayed armed would mute a
                // genuine later edit to the same item, silently and forever, which is strictly worse
                // than the loop it was added to stop.
                //
                // WHAT IT DOES NOT BOUND, because the two arities do not match: the token is one per
                // ENTITY and consumed once, while a save publishes one event per renamed FILE. An
                // entity with more than one acting file therefore leaves a surplus event unsuppressed
                // in every generation, and under a destination pair that never reaches a fixed point
                // that survivor re-plans and publishes a full set of its own — so the chain sustains
                // itself generation after generation instead of ending. This suppression bounds the
                // chain only where one save raises one event, which is the single-file case.
                //
                // Arming a COUNT instead is the obvious remedy and is deliberately not taken: a
                // leftover token mutes a genuine later edit with no symptom at all, which is a worse
                // failure than a relocation the user can see.
                if (_selfSaved.TryRemove(selfSaveKey, out _))
                {
                    return;
                }

                var db = services.GetRequiredService<DbContext>();
                var port = new CoveRenamerDataPort(db, _coveConfig);

                // Route auto-renamer IDENTICALLY to the manual batch and to /preview. Build the same
                // RouteLookups from the same RenamerOptions and use the routing overload, so a matched
                // studio/tag/path rule relocates the just-edited item to its configured destination — the
                // same on-disk outcome the user previews and the batch executes.
                //
                // This does NOT enable dribble-relocate of the whole library: only the entity just
                // edited is planned, and it lands where preview and the batch would put it — a matched
                // rule's own destination, or the DEFAULT *Where files go* for an item no rule matched
                // (RouteCategory.Unmatched), which leaves the item in place only while that default names
                // neither a root nor a folder template. Either way the move passes the allowlist/canonical
                // confinement gate via the routed anchor.
                //
                // It does NOT follow that the item then settles. A source-path rule matches on where the
                // file IS, so once the rule has moved the file the rule no longer matches and the default
                // takes the item back: the two destinations ALTERNATE rather than compete, and a default
                // rooted exactly at a rule's source path with an EMPTY folder template names the very
                // folder that rule empties, so the pair never reaches a fixed point at all. What bounds
                // the damage is the self-save suppression above, not convergence — and only as far as
                // that suppression reaches, which its own site states.
                //
                // The accepted cost, so it is never read as an oversight: on a single-file item such a
                // pair still moves it ONE HOP PER ACTION — one per manual "Rename all" click, one per
                // external edit — and nothing in the panel names the cause. The pair is exactly
                // decidable at save time (a default whose folder EQUALS a rule's source path while its
                // folder template is empty) and refusing it there is deliberately not done here.
                // Preview, auto-renamer, and batch all resolve destinations identically.
                var lookups = BuildLookups(options);
                var plan = await new RenamerPlanner(port).PlanAsync(kind, entityId, options, lookups, ct);

                // The NARROWER of the two guards, and not redundant with the suppression above: this one
                // stops a pass that would act on nothing from doing anything at all — no batch opened, no
                // run id minted, no executor call — while the suppression stops a pass this handler's own
                // save caused. Gated items land here as SkipGated (only-organized / require-fields
                // respected) and are likewise skipped. On a configuration that converges this guard is
                // what ends the chain; on one that does not, it never fires, which is why it cannot be
                // the re-entrancy guard on its own.
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

                // Armed BEFORE the call, never after. The host's re-raise is fire-and-forget and can
                // arrive while ExecuteAsync is still running, so a token set afterwards is a race the
                // event wins — and the one it wins is the loop.
                _selfSaved[selfSaveKey] = 0;
                RenamerExecutor.RenamerRunResult result;
                try
                {
                    // Single-entity hook path (no batch concurrency): no pre-resolved folder map — the
                    // executor resolves the destination folder itself, safe because this call is not
                    // parallelized.
                    result = await executor.ExecuteAsync(plan, options, ct: ct);
                }
                catch
                {
                    // Disarm on any escape, cancellation included. A throw says nothing about whether a
                    // save landed, and a token nothing consumes swallows the next genuine edit — this
                    // design's one failure mode. Erring toward one extra hop beats erring toward a
                    // silently muted hook.
                    _selfSaved.TryRemove(selfSaveKey, out _);
                    throw;
                }

                if (result.Renamed.Count == 0)
                {
                    // Nothing saved ⇒ no event will be re-raised ⇒ nothing will consume the token.
                    // Disarm immediately, for the same reason as the catch above: a leaked suppression
                    // is what this guard must not become.
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
