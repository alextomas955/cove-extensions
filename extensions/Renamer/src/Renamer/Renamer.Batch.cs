using Cove.Extensions.Shared;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer;

// One rename run's fixed settings, carried whole from the entry point down to the chunk body. The
// three methods below take it instead of repeating its fields, which they had grown to nine
// arguments of. A null FreeSpaceProbe measures the real volume.
internal sealed record RenameRun(
    RenamerFileKind Kind,
    int TotalEntities,
    string OperationId,
    RenamerOptions Options,
    Func<string, long>? FreeSpaceProbe = null,
    int ChunkEntities = Renamer.RenameChunkEntities);

/// <summary>
/// The selected-item rename path: the "Rename selected" action's job body. It splits the selection
/// into chunks, plans and executes each one through <see cref="RenamerExecutor"/>, and journals what
/// it moved so undo can replay it. The whole-library counterpart is <c>Renamer.Library.cs</c>.
/// </summary>
public sealed partial class Renamer
{
    // The fraction of a chunk's progress bar the planning pass owns, execution taking the rest. The
    // split is cosmetic: both passes scale linearly, so the bar only advances.
    private const double PlanningProgressShare = 0.5;

    // The entities a rename run plans and executes before starting the next chunk. Equal to
    // MaxEntityIdsPerRequest, so one selection is one chunk. A run's plans, projected moves and
    // destination-folder map are released with each chunk, so a whole-library run costs what one full
    // selection costs.
    internal const int RenameChunkEntities = MaxEntityIdsPerRequest;

    // One acting file's unit of execution work. The move tuple partitions same- from cross-volume
    // and re-checks free space in flight; the entity id is for per-item logging.
    private readonly record struct BatchUnit(
        int EntityId,
        Planner.RenamerPlan Plan,
        (string OldFullPath, string NewFullPath, long SizeBytes) Move);

    // What one chunk did. Shortfall, when set, is the free-space refusal that stops the run.
    private readonly record struct ChunkOutcome(
        int Renamed, int Skipped, int Failed, int ContestedFiles, string? Shortfall);

    // Maps one chunk's own progress onto its share of a run over `total` entities.
    private sealed class ChunkSliceProgress(IJobProgress inner, int offset, int share, int total) : IJobProgress
    {
        public void Report(double percent, string? message = null)
            => inner.Report(
                Math.Clamp((offset + (Math.Clamp(percent, 0d, 1d) * share)) / Math.Max(total, 1), 0d, 1d),
                message);
    }

    // The free-space reading the up-front refusal and the in-flight re-check share. An unprobeable
    // volume reads as long.MaxValue and never blocks a run: the reading is a pre-flight courtesy, and
    // the cross-volume mover verifies every copy and fails each item safely when the disk is full.
    // DriveInfo throws for a root that is not a drive letter, such as a UNC share reached through an
    // allowed root, and the reading can hit a transient IO error on an offline volume.
    private static long AvailableFreeSpace(string volume)
    {
        try
        {
            return new DriveInfo(volume).AvailableFreeSpace;
        }
        catch (ArgumentException)
        {
            return long.MaxValue;
        }
        catch (IOException)
        {
            return long.MaxValue;
        }
    }

    // Renames every selected id. One selection is one user action, so this call is its own operation
    // and everything it renames comes back from a single undo. An empty selection is a no-op that
    // still reports the final 1.0.
    internal async Task RunRenamerBatchAsync(
        RenamerFileKind kind, IReadOnlyList<int> ids, IJobProgress progress, CancellationToken ct,
        Func<string, long>? freeSpaceProbe = null)
    {
        if (ids.Count == 0)
        {
            progress.Report(1d, "Nothing to rename.");
            return;
        }

        var options = await new OptionsStore(Store, _log).LoadAsync(ct);

        int taken = 0;
        Task<IReadOnlyList<int>> NextChunk(CancellationToken token)
        {
            IReadOnlyList<int> chunk = [.. ids.Skip(taken).Take(RenameChunkEntities)];
            taken += chunk.Count;
            return Task.FromResult(chunk);
        }

        var run = new RenameRun(
            kind, ids.Count, Guid.NewGuid().ToString("N"), options, freeSpaceProbe);
        await RunRenameChunksAsync(run, NextChunk, progress, ct);
    }

    // Renames every entity of one kind, walking its ids a page at a time through the same chunk the
    // selection path drives. totalEntities is a snapshot taken before the walk, so the fraction it
    // scales drifts from what the pages yield and the caller's final report lands the bar. The
    // cursor is the entity id, which a rename never changes, so the run's own writes can neither
    // skip a page nor repeat one. A cancellation between chunks leaves earlier chunks done and
    // undoable.
    internal Task<string?> RunRenamerKindAsync(
        RenameRun run, AllowedIds allowedIds, IJobProgress progress, CancellationToken ct)
    {
        int after = 0;

        // Allowed ids one chunk had no room for. Bounded by a single database page, so it does not
        // grow with the library.
        var carried = new Queue<int>();

        // The cursor advances by the database page, never by what survives the caller's per-entity
        // write check, so no id is examined twice. Pages are drawn until the chunk is full or the
        // kind runs out, because a short chunk is how the run below reads exhaustion: returning a
        // partly denied page directly would end the walk at the first denial.
        //
        // Every draw is a whole page and never the chunk's remaining capacity: a page sized to the
        // room left costs one database read and one authorization call per denied entity over a long
        // denied region. Ids are carried only by an iteration that fills the chunk, so a chunk
        // shorter than chunkEntities still implies an empty queue and an exhausted kind.
        async Task<IReadOnlyList<int>> NextPageAsync(CancellationToken token)
        {
            var allowed = new List<int>(run.ChunkEntities);
            while (allowed.Count < run.ChunkEntities && carried.Count > 0)
            {
                allowed.Add(carried.Dequeue());
            }

            while (allowed.Count < run.ChunkEntities)
            {
                var page = await RunAsSystem.RunInSystemScopeAsync(
                    ScopeFactory,
                    services => new CoveRenamerDataPort(services.GetRequiredService<DbContext>(), _coveConfig)
                        .LoadEntityIdPageAsync(run.Kind, after, run.ChunkEntities, token));

                if (page.Count == 0)
                {
                    break;
                }

                after = page[^1];

                foreach (int id in await allowedIds(run.Kind, page, token))
                {
                    if (allowed.Count < run.ChunkEntities)
                    {
                        allowed.Add(id);
                    }
                    else
                    {
                        carried.Enqueue(id);
                    }
                }
            }

            return allowed;
        }

        return RunRenameChunksAsync(run, NextPageAsync, progress, ct);
    }

    // Drives nextChunk to exhaustion through the shared chunk body, tallies what the chunks did and
    // reports the run's final 1.0 with what happened. Returns the free-space shortfall that stopped
    // the run, or null when it ran to the end.
    private async Task<string?> RunRenameChunksAsync(
        RenameRun run,
        Func<CancellationToken, Task<IReadOnlyList<int>>> nextChunk,
        IJobProgress progress,
        CancellationToken ct)
    {
        // Resolved once for the run: an absent probe reads the real volume, and the chunk body must
        // not have to decide that per chunk.
        var freeSpaceProbe = run.FreeSpaceProbe ?? AvailableFreeSpace;

        // Hoisted once for the whole run: the studio-id, tag-name and exact-path dictionaries and the
        // pre-parsed source-path regex set, so the resolver never re-walks or re-compiles them per
        // entity. An invalid user regex is caught at this build step and skipped with a log, so it can
        // never throw mid-match.
        var lookups = RouteLookups.From(run.Options, LogInvalidRouteRegex);

        // The journal gets its own scope, and therefore its own DbContext, for the whole run: every
        // parallel worker of every chunk shares it because it mints each row's sequence number, and a
        // DbContext is not thread-safe, so it cannot ride on a worker's scope. Its writes need no
        // elevation because its two tables are extension-owned and carry none of CoveContext's
        // per-principal query filters.
        await using var journalScope = ScopeFactory.CreateAsyncScope();
        await using var journal = new CoveRevertJournal(journalScope.ServiceProvider.GetRequiredService<DbContext>());

        int renamed = 0, skipped = 0, failed = 0, contested = 0, entitiesDone = 0;
        string? shortfall = null;

        while (true)
        {
            // Checked between chunks as well as inside them, so a cancellation stops the run cleanly
            // with everything already renamed still renamed and still journalled.
            ct.ThrowIfCancellationRequested();

            var chunk = await nextChunk(ct);
            if (chunk.Count == 0)
            {
                break;
            }

            var outcome = await RunRenameChunkAsync(
                run, chunk, lookups, journal, freeSpaceProbe,
                new ChunkSliceProgress(progress, entitiesDone, chunk.Count, run.TotalEntities), ct);

            renamed += outcome.Renamed;
            skipped += outcome.Skipped;
            failed += outcome.Failed;
            contested += outcome.ContestedFiles;
            entitiesDone += chunk.Count;

            if (outcome.Shortfall is not null)
            {
                shortfall = outcome.Shortfall;
                break;
            }

            if (chunk.Count < run.ChunkEntities)
            {
                break;
            }
        }

        if (shortfall is not null)
        {
            // What the run did before it stopped stays done and stays undoable, so the message names it:
            // a bare refusal would read as though the whole run had been declined.
            progress.Report(
                1d,
                $"Refused: insufficient free space ({shortfall}). {renamed} file(s) renamed before the run stopped.{RefusedNote(contested)}");
            return shortfall;
        }

        if (renamed == 0 && failed == 0 && skipped == contested)
        {
            progress.Report(1d, $"Nothing to rename.{RefusedNote(contested)}");
            return null;
        }

        progress.Report(1d, $"Rename complete.{RefusedNote(contested)}");
        return null;
    }

    // Plans one chunk of ids over a single read-only scope, refuses it if a destination volume would
    // not fit, then executes what acts in parallel. Both a selection and a whole-library walk run
    // through here.
    //
    // Same-volume renames are bounded by SameVolumeConcurrency and cross-volume copies by
    // CrossVolumeConcurrency within one (source, destination) disk pair; the pairs run one after
    // another, so peak concurrency is one pair's bound and never the sum over pairs. Each worker
    // opens its own scope and resolves its own DbContext: a DbContext is not thread-safe and Cove
    // disables EF's thread-safety checks, so a shared one corrupts silently.
    private async Task<ChunkOutcome> RunRenameChunkAsync(
        RenameRun run,
        IReadOnlyList<int> ids,
        RouteLookups lookups,
        CoveRevertJournal journal,
        Func<string, long> freeSpaceProbe,
        IJobProgress progress,
        CancellationToken ct)
    {

        // A fresh run id per chunk, and the operation id constant across the run. An undo acts on the
        // operation, so however many batches a run opens, the user has one action to reverse.
        var runId = Guid.NewGuid().ToString("N");

        LogBatchStarted(runId, run.Kind, ids.Count);

        // Planning reads only. It is sequential for deterministic preview ordering, it mutates nothing
        // the workers race, and it writes nothing at all, so a chunk refused below leaves the database
        // as it found it.
        var planned = new List<BatchUnit>();

        // Planning reports no percentage of its own until the loop starts, so trace it to the log -
        // otherwise a large chunk sits at its opening percentage with no signal that it is still planning.
        LogPlanningStarted(runId, run.Kind, ids.Count);

        // One elevated span for the whole planning pass, not one per entity: the background principal is
        // anonymous, and an unelevated read returns zero rows with no error.
        await RunAsSystem.RunInSystemScopeAsync(ScopeFactory, async services =>
        {
            var readDb = services.GetRequiredService<DbContext>();
            var port = new CoveRenamerDataPort(readDb, _coveConfig);
            var planner = new RenamerPlanner(port);

            // The chunk's entities in one bounded set of round-trips, the same shape the library scan
            // uses, rather than one entity-graph load per id. The walk below still follows the caller's
            // id order, so the preview order and the units it produces do not depend on what the
            // database returned first. An id naming an entity the load did not return vanished between
            // the id list and this read, and contributes nothing.
            var loaded = await port.LoadEntitiesAsync(run.Kind, ids, ct);
            var byId = new Dictionary<int, RenamerEntity>(loaded.Count);
            foreach (var entity in loaded)
            {
                byId[entity.EntityId] = entity;
            }

            int planIndex = 0;
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();

                int actingThisItem = 0;
                if (byId.TryGetValue(id, out var entity))
                {
                    var plan = await planner.PlanLoadedEntity(entity, run.Options, lookups, ct);

                    // File sizes for the free-space sum live on the loaded entity's files, not on the
                    // plan item, so they are read off the entity the plan was built from.
                    var sizeByFileId = entity.Files.ToDictionary(f => f.FileId, f => f.SizeBytes);

                    foreach (var item in plan.Items)
                    {
                        if (item.Status is not (RenamerStatus.Rename or RenamerStatus.Move))
                        {
                            continue;
                        }

                        actingThisItem++;
                        long size = sizeByFileId.GetValueOrDefault(item.FileId);
                        // Each worker is handed a single-file plan so the executor acts on exactly this
                        // file; the parent entity id rides the unit for logging.
                        var unitPlan = new RenamerPlan(plan.EntityId, plan.Kind, [item]);
                        planned.Add(new BatchUnit(plan.EntityId, unitPlan,
                            (item.OldFullPath, item.NewFullPath, size)));
                    }
                }

                LogItemPlanned(runId, ++planIndex, ids.Count, id, actingThisItem);
                // Planning drives the first half of the chunk's bar; execution drives the second, so the
                // bar only ever advances. The message names the phase, so the UI reads "Planning 769/1000"
                // rather than a silent 0%.
                progress.Report(
                    (double)planIndex / ids.Count * PlanningProgressShare,
                    $"Planning {planIndex}/{ids.Count}...");
            }
        });

        // One acting unit per source file. Naming the same entity twice in one request plans its files
        // twice, and both units are then the same work, so the file is scheduled once.
        //
        // Grouped by the slice's file-identity rule, which ignores case on Windows and macOS. On a
        // volume formatted case-sensitive there, two rows differing only in case are two files and both
        // are refused: the refusal is recoverable by hand, while renaming one row's file out from under
        // another's is not.
        var bySourcePath = planned
            .GroupBy(u => PathOps.NormalizeSlash(u.Move.OldFullPath), PathOps.PathComparer)
            .ToList();

        // Two different file rows naming one source path is database state a rename cannot arbitrate:
        // acting on either moves the file the other row also claims. Every row of such a group is
        // refused and named in the log, so the anomaly is reported rather than half-applied. The
        // database answers this, because the twin row can sit in another chunk or under another kind,
        // where a grouping over what was just planned cannot see it.
        IReadOnlyDictionary<string, int> claimsByPath = new Dictionary<string, int>();
        if (bySourcePath.Count > 0)
        {
            claimsByPath = await RunAsSystem.RunInSystemScopeAsync(ScopeFactory, services =>
                new CoveRenamerDataPort(services.GetRequiredService<DbContext>(), _coveConfig)
                    .CountSourcePathClaimsAsync([.. bySourcePath.Select(g => g.Key)], ct));
        }

        var acting = new List<BatchUnit>(planned.Count);
        int contestedFiles = 0;
        foreach (var claimants in bySourcePath)
        {
            int rows = claimants.Select(u => u.Plan.Items[0].FileId).Distinct().Count();
            int claims = Math.Max(rows, claimsByPath.GetValueOrDefault(claimants.Key));
            if (claims == 1)
            {
                acting.Add(claimants.First());
                continue;
            }

            contestedFiles += rows;
            LogContestedSourcePath(runId, claimants.Key, claims);
        }

        // Sum the projected cross-volume bytes per destination volume and refuse before touching disk if
        // a volume would not fit. Same-volume moves are excluded from the sum by the guard. This runs
        // before any batch is opened, so a refused chunk opens no journal batch.
        var shortfall = FreeSpaceGuard.Shortfall(
            acting.Select(u => u.Move), run.Options.FreeSpaceHeadroomBytes, freeSpaceProbe);
        if (shortfall.Count > 0)
        {
            string detail = string.Join("; ",
                shortfall.Select(s => $"{s.Volume}: need {s.Needed} bytes, {s.Available} free"));
            LogBatchDone(runId, 0, contestedFiles, 0);
            return new ChunkOutcome(0, contestedFiles, 0, contestedFiles, detail);
        }

        // Nothing acts, so open no batch: an empty batch would shadow the operation's earlier replayable
        // one when /undo looks for work.
        if (acting.Count == 0)
        {
            LogBatchDone(runId, 0, contestedFiles, 0);
            return new ChunkOutcome(0, contestedFiles, 0, contestedFiles, null);
        }

        // Resolve or create every distinct destination folder once, single-threaded, after the refusals
        // above: folder creation is persistent shared state, so it happens only for a chunk that will now
        // run, and never inside the parallel execution below. Each worker reads its move's destination id
        // from this map, so no two of them check-then-act on a shared Folder row. An in-place rename uses
        // the source folder id and needs no entry.
        var folderIdByPath = new Dictionary<string, int>(DestinationResolver.SourcePathComparer);
        await RunAsSystem.RunInSystemScopeAsync(ScopeFactory, async services =>
        {
            var port = new CoveRenamerDataPort(services.GetRequiredService<DbContext>(), _coveConfig);
            foreach (var unit in acting)
            {
                var planItem = unit.Plan.Items[0];
                if (planItem.Status == RenamerStatus.Move
                    && !folderIdByPath.ContainsKey(planItem.TargetFolderPath))
                {
                    folderIdByPath[planItem.TargetFolderPath] =
                        await port.GetOrCreateFolderIdAsync(planItem.TargetFolderPath, ct);
                }
            }
        });

        // Now, and only now, open exactly one batch: the chunk produced acting work and it fits.
        await journal.BeginBatchAsync(runId, run.OperationId, run.Kind, DateTime.UtcNow, ct);

        // Marks the planning/execution boundary in the log: the percentage now advances per completed
        // file, so a later stall is legible as "stuck partway through {Acting}", not as silence.
        LogPlanningDone(runId, acting.Count, ids.Count);

        // Partitioned, bounded, one scope per worker. The partitions carry the units themselves, so every
        // unit is scheduled exactly once whatever its paths are.
        var partitions = FreeSpaceGuard.PartitionByPair(
            acting, u => (u.Move.OldFullPath, u.Move.NewFullPath));

        // Serializes every concurrent progress report. The workers call Report from many threads at once,
        // and nothing establishes that the host's sink is thread-safe - a host that appends to a list or
        // writes a SignalR message without its own lock could corrupt state or interleave messages. The
        // done counter is already interlocked; this guards only the host-facing call itself.
        var progressGate = new object();

        int totalRenamed = 0, totalSkipped = contestedFiles, totalFailed = 0;
        int done = 0;
        int totalUnits = Math.Max(acting.Count, 1);

        async ValueTask RunUnitAsync(BatchUnit unit, CancellationToken token)
        {
            // Cross-volume only: re-check free space just before the copy, so a concurrent scanner that
            // shrank the destination since planning skips this item gracefully rather than filling the
            // disk. Same-volume moves consume ~no space and are excluded by the guard.
            var inFlight = FreeSpaceGuard.Shortfall([unit.Move], run.Options.FreeSpaceHeadroomBytes, freeSpaceProbe);
            if (inFlight.Count > 0)
            {
                Interlocked.Increment(ref totalSkipped);
                // A free-space refusal is neither a lock nor a collision, so it carries the dedicated
                // SkipNoSpace status and log output attributes a disk-full skip correctly.
                LogItemSkipped(runId, run.Kind, unit.EntityId, RenamerStatus.SkipNoSpace,
                    "skipped: destination volume dropped below free-space headroom in flight");
                Interlocked.Increment(ref done);
                ReportProgress((double)Volatile.Read(ref done) / totalUnits);
                return;
            }

            // Own scope per worker, so own DbContext, port and executor. The shared journal is passed in
            // and its writes are serialized. The executor classifies rather than throws, so a per-item
            // fault is a skip or a failure recorded below and only a genuine cancellation propagates.
            //
            // The move is logged before it runs: a cross-volume copy of a large file can take many
            // seconds, and completions alone make a long gap read as a freeze. The cross-volume flag and
            // the size come from the already-known move tuple, so this costs no extra IO.
            bool crossVolume = !VolumeClassifier.SameVolume(unit.Move.OldFullPath, unit.Move.NewFullPath);
            long sizeMb = unit.Move.SizeBytes / (1024 * 1024);
            int doneNow = Volatile.Read(ref done);
            LogItemStarting(runId, doneNow, totalUnits, run.Kind, unit.EntityId,
                crossVolume, sizeMb, unit.Move.OldFullPath);

            var result = await RunAsSystem.RunInSystemScopeAsync(ScopeFactory, services =>
            {
                var db = services.GetRequiredService<DbContext>();
                var exec = new RenamerExecutor(
                    new CoveRenamerDataPort(db, _coveConfig), EventBus, journal, runId);
                return exec.ExecuteAsync(unit.Plan, run.Options, folderIdByPath, token);
            });
            LogBatchItem(runId, run.Kind, unit.EntityId, result);

            // Thread-safe tally: a racing `+=` would lose increments under parallel workers.
            Interlocked.Add(ref totalRenamed, result.Renamed.Count);
            Interlocked.Add(ref totalSkipped, result.Skipped.Count);
            Interlocked.Add(ref totalFailed, result.Failed.Count);

            Interlocked.Increment(ref done);
            ReportProgress((double)Volatile.Read(ref done) / totalUnits);
        }

        void ReportProgress(double percent)
        {
            // Execution owns the second half of the chunk's bar: its own [0,1] completion fraction maps
            // into [PlanningProgressShare, 1.0], so it picks up where planning left off and never jumps
            // backwards. A message-less report keeps the host's own phase label.
            double scaled = PlanningProgressShare + percent * (1d - PlanningProgressShare);
            lock (progressGate)
            {
                progress.Report(scaled, null);
            }
        }

        foreach (var (pair, pairUnits) in partitions)
        {
            ct.ThrowIfCancellationRequested();

            // The same-volume group is bounded by SameVolumeConcurrency, a pressure bound and not a space
            // guard, since same-drive moves are instant metadata renames. A value <= 0 means unbounded and
            // maps to Parallel's -1 sentinel. Each cross-volume (source,destination) pair is bounded by
            // the configured per-pair concurrency.
            int sameVolumeDegree = run.Options.SameVolumeConcurrency > 0 ? run.Options.SameVolumeConcurrency : -1;
            int degree = pair == FreeSpaceGuard.SameVolumePair
                ? sameVolumeDegree
                : run.Options.CrossVolumeConcurrency;
            await Parallel.ForEachAsync(pairUnits,
                new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
                RunUnitAsync);
        }

        LogBatchDone(runId, totalRenamed, totalSkipped, totalFailed);
        return new ChunkOutcome(totalRenamed, totalSkipped, totalFailed, contestedFiles, null);
    }

    // The refusal has to reach the job's own message: its files rename nothing and produce no per-item
    // result, so a log line is the only other place it appears.
    private static string RefusedNote(int contestedFiles) =>
        contestedFiles > 0
            ? $" {contestedFiles} file(s) refused: more than one record names the same file."
            : "";

    /// <summary>
    /// Records one planned entity's per-file outcomes to the host log: a line per renamed/moved file
    /// (old → new), per skip (with its reason), and per failure. Paths are logged so a maintainer can
    /// audit exactly what moved and revert from the log if needed.
    /// </summary>
    private void LogBatchItem(string runId, RenamerFileKind kind, int entityId, RenamerExecutor.RenamerRunResult result)
    {
        foreach (var r in result.Renamed)
        {
            LogItemRenamed(runId, kind, entityId, r.Status, r.OldPath, r.NewPath);
            if (r.Reason is { Length: > 0 } warning)
            {
                LogItemRenamedWithWarning(runId, kind, entityId, warning);
            }
        }

        foreach (var s in result.Skipped)
        {
            LogItemSkipped(runId, kind, entityId, s.Status, s.Reason ?? "no reason given");
        }

        foreach (var f in result.Failed)
        {
            LogItemFailed(runId, kind, entityId, f.OldPath, f.NewPath, f.Reason ?? "no reason given");
        }
    }
}
