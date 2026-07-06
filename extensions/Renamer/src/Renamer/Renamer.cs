using System.Text.RegularExpressions;
using Cove.Core.Events;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Renamer.Execution;
using Renamer.Jobs;
using Renamer.Options;
using Renamer.Planner;

namespace Renamer;

public sealed partial class Renamer : FullExtensionBase
{
    public override string Id => "com.alextomas955.renamer";
    public override string Name => "Renamer";

    // Repo-committed dev placeholders, not release-stamped: the published artifact's real version
    // comes from the release tag (build.yml's -p:Version= and the packaged extension.json/
    // package.json stamps). scripts/check-version-parity.mjs reconciles these against
    // extension.json, package.json, and the catalog registry manifest so they can't drift.
    public override string Version => "0.1.0";
    public override string? Description => "Bulk-renames Cove library items using configurable patterns.";
    public override string? Author => "alextomas955";
    public override string? Url => "https://github.com/alextomas955/renamer";
    public override IReadOnlyList<string> Categories => [ExtensionCategories.Tools, ExtensionCategories.Automation];
    public override string? MinCoveVersion => "0.7.1";

    // ── Executor wiring ───────────────────────────────────────────────────────
    // The executor needs a SCOPED CoveContext per run (a DbContext is scoped, not singleton) and the
    // host IEventBus for the post-renamer reindex event. Capture the scope factory + event bus
    // in InitializeAsync; a run opens its own scope via CreateAsyncScope() and resolves the scoped
    // DbContext there.

    // Resolved once at load and never null afterwards. The fields are nullable only because they are
    // assigned in InitializeAsync rather than the ctor; every use site is reached only after init, so
    // the non-null accessors below are the single, guarded way the rest of the extension reads them.
    private IServiceScopeFactory? _scopeFactory;
    private IEventBus? _eventBus;

    /// <summary>
    /// The host logger, writing to Cove's normal log. Renames and moves change files on disk, so every
    /// batch/undo/auto-renamer records what it did (per-file old → new, skip reasons, a summary) for
    /// audit and troubleshooting. Non-null by construction: defaults to a no-op logger and is replaced
    /// in <see cref="InitializeAsync"/> if the host supplies one, so the source-generated
    /// <c>[LoggerMessage]</c> methods in <c>Renamer.Logging.cs</c> never dereference null and a missing
    /// host logger never blocks a renamer. (The generator binds to this field by its <c>ILogger</c> type.)
    /// </summary>
    private ILogger _log = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    /// <summary>The scope factory captured at init. Throws if read before initialization.</summary>
    private IServiceScopeFactory ScopeFactory =>
        _scopeFactory ?? throw new InvalidOperationException(
            "Renamer extension used before InitializeAsync ran (IServiceScopeFactory not captured).");

    /// <summary>The host event bus captured at init. Throws if read before initialization.</summary>
    private IEventBus EventBus =>
        _eventBus ?? throw new InvalidOperationException(
            "Renamer extension used before InitializeAsync ran (IEventBus not captured).");

    public override Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        // Resolve the captured seams with GetRequiredService so a missing host registration fails
        // clearly here, at load, instead of surfacing as a NullReferenceException at first use.
        _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        _eventBus = services.GetRequiredService<IEventBus>();
        // Logging is optional: the host forwards ILogger into the extension scope, but treat its
        // absence as non-fatal (GetService, not GetRequiredService) — a renamer must still run. Keep the
        // NullLogger default when the host supplies none.
        _log = services.GetService<ILogger<Renamer>>() ?? _log;
        return base.InitializeAsync(services, ct);
    }

    // ── Shared batch core ─────────────────────────────────────────────────────
    // The SINGLE method the job runner and the /renamer enqueued delegate both
    // call. A THIN adapter over the planner+executor: decode → scope → loop → report. No
    // renamer/move/collision/rollback logic lives here.

    /// <summary>
    /// Maps a Cove entity-type string (lowercase singular) to a <see cref="RenamerFileKind"/>.
    /// Supports <c>video</c>/<c>image</c>/<c>audio</c> (case-insensitive); everything
    /// else — including <c>gallery</c> (unsupported) and unknown — returns false with the
    /// kind defaulted. NOT <c>Enum.Parse</c>: Cove's type strings do not map 1:1 to the enum names.
    /// </summary>
    internal static bool TryParseKind(string? entityType, out RenamerFileKind kind)
    {
        switch (entityType?.ToLowerInvariant())
        {
            case "video": kind = RenamerFileKind.Video; return true;
            case "image": kind = RenamerFileKind.Image; return true;
            case "audio": kind = RenamerFileKind.Audio; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>
    /// Maps a <see cref="RenamerFileKind"/> to the host read/write permission pair that gates operating
    /// on that entity kind. Cove models entity permissions per-kind (<c>videos.*</c>/<c>images.*</c>/
    /// <c>audios.*</c>), so a renamer of an image must require <c>images.write</c> — not the video
    /// permission. <c>Gallery</c> is not a renamable kind; it is mapped to the video pair only so the
    /// switch is total, and it never reaches an endpoint (<see cref="TryParseKind"/> rejects it).
    /// </summary>
    internal static (string Read, string Write) PermissionsFor(RenamerFileKind kind) => kind switch
    {
        RenamerFileKind.Image => (Cove.Core.Auth.Permissions.ImagesRead, Cove.Core.Auth.Permissions.ImagesWrite),
        RenamerFileKind.Audio => (Cove.Core.Auth.Permissions.AudiosRead, Cove.Core.Auth.Permissions.AudiosWrite),
        _ => (Cove.Core.Auth.Permissions.VideosRead, Cove.Core.Auth.Permissions.VideosWrite),
    };

    /// <summary>
    /// Adapts the host's core <see cref="Cove.Core.Interfaces.IJobProgress"/> (handed to the
    /// <c>IJobService.Enqueue</c> delegate) to the extension <see cref="IJobProgress"/> the shared
    /// batch method consumes — mirrors the host's <c>JobProgressBridge</c> (Report-only).
    /// </summary>
    private sealed class HostProgress(Cove.Core.Interfaces.IJobProgress core) : IJobProgress
    {
        public void Report(double percent, string? message = null) => core.Report(percent, message);
    }

    /// <summary>
    /// One acting file's unit of work for PHASE B: a single-file plan the worker hands the executor,
    /// the projected move tuple (used to partition same- vs cross-volume and to re-check free space in
    /// flight), and the parent entity id for per-item logging.
    /// </summary>
    private readonly record struct BatchUnit(
        int EntityId,
        Planner.RenamerPlan Plan,
        (string OldFullPath, string NewFullPath, long SizeBytes) Move);

    /// <summary>
    /// The fraction of the progress bar the PHASE A planning pass owns. Planning every id in a large
    /// library is slow and reported nothing before, so the bar sat at 0% for the whole pass; splitting
    /// the bar (planning 0 → this, executing this → 1.0) keeps it moving throughout. 0.5 splits it evenly;
    /// the exact split is cosmetic — both phases scale linearly, so the bar only ever advances.
    /// </summary>
    private const double PlanningProgressShare = 0.5;

    /// <summary>
    /// Renames every id in the decoded batch in two phases. PHASE A plans + classifies ALL ids
    /// sequentially over ONE read-only scope (deterministic preview ordering) and refuses the batch up
    /// front if a destination volume would not fit. PHASE B executes the acting items in parallel:
    /// same-volume renames run unthrottled (an instant metadata <c>File.Move</c> needs no throttle and
    /// consumes ~no extra space), cross-volume copies run bounded per (source,dest) disk pair. EACH
    /// worker opens its OWN scope and resolves its OWN <see cref="DbContext"/> — a <c>DbContext</c> is
    /// not thread-safe and Cove disables EF's thread-safety checks, so a shared context would corrupt
    /// silently; per-worker scopes make isolation structural. The ONE shared object is the
    /// <see cref="RevertLog"/>, whose appends are serialized (it is a read-modify-write on a single
    /// blob) so the undo record never tears under parallel workers. Bad/empty/unsupported input is a
    /// clean no-op that still reports the final <c>1.0</c> — never throws on untrusted job parameters.
    /// </summary>
    /// <param name="parameters">The host's string-only job parameter map (entity type + id list).</param>
    /// <param name="progress">The job-progress sink reported during PHASE B and a final <c>1.0</c>.</param>
    /// <param name="ct">Cancellation token; a genuine cancellation aborts the run.</param>
    /// <param name="freeSpaceProbe">
    /// The available-free-space probe used by both the up-front refusal and the in-flight re-check.
    /// Defaults to the real <c>vol =&gt; new DriveInfo(vol).AvailableFreeSpace</c>; tests inject a
    /// deterministic fake so the free-space paths are exercisable with no real second drive.
    /// </param>
    internal async Task RunRenamerBatchAsync(
        IReadOnlyDictionary<string, string>? parameters, IJobProgress progress, CancellationToken ct,
        Func<string, long>? freeSpaceProbe = null)
    {
        var (entityType, ids) = RenamerJob.Decode(parameters);

        if (!TryParseKind(entityType, out var kind) || ids.Length == 0)
        {
            progress.Report(1d, "Nothing to renamer.");
            return;
        }

        // The only disk touch of the free-space guard. The public job-facing call path passes no probe
        // and gets the real DriveInfo reading; tests pass a controlled function. DriveInfo's ctor
        // throws ArgumentException for a non-drive-letter root (e.g. a UNC \\server\share destination
        // reachable via an AllowedRoot), and the probe can hit transient IO errors. This guard is a
        // best-effort pre-flight courtesy — the cross-volume mover still verifies every copy and fails
        // each item safely on a real ENOSPC (copy→verify→delete-source-last never loses the source).
        // So an unprobeable volume returns long.MaxValue ("don't block here; let the mover handle it")
        // rather than throwing out and failing the whole batch.
        freeSpaceProbe ??= vol =>
        {
            try
            {
                return new DriveInfo(vol).AvailableFreeSpace;
            }
            catch (ArgumentException)
            {
                return long.MaxValue; // non-drive-letter root (UNC/rootless) — not probeable via DriveInfo
            }
            catch (IOException)
            {
                return long.MaxValue; // transient/offline volume — defer to the mover's per-item verify
            }
        };

        var options = await new OptionsStore(Store).LoadAsync(ct);

        // Hoist the routing lookups ONCE per batch: the studio-id / tag-name / exact-path dicts and
        // the PRE-PARSED source-path regex set, so the resolver never re-walks/re-compiles per entity.
        // An invalid user regex is caught HERE (build time) and skipped-with-a-log, never thrown
        // mid-match (classify, don't throw at the batch boundary).
        var lookups = BuildLookups(options);

        // One action click = one selection = one /renamer = one job = one batch, all one kind.
        // Mint a fresh runId AFTER the kind/ids validation passed (so the early no-op return above
        // opens no batch). The batch HEADER is NOT written yet: a header written before PHASE A knows
        // whether anything acts would leave an EMPTY open batch that ReadLastOpenBatchAsync returns as
        // "the last open batch", shadowing a genuinely-replayable earlier batch from /undo.
        // We defer BeginBatchAsync until PHASE A has produced at least one acting unit AND the batch
        // cleared the free-space refusal, so an all-skip or refused batch opens no header at all. The
        // same runId + RevertLog is then passed into EVERY worker's executor so every per-success
        // AppendAsync row accumulates under this single open batch.
        var runId = Guid.NewGuid().ToString("N");
        var revertLog = new RevertLog(Store);

        LogBatchStarted(runId, kind, ids.Length);

        // ── PHASE A — sequential, read-only: plan + classify every id, capture file sizes ──────────
        // Kept sequential (not parallelized) for deterministic preview ordering; planning mutates
        // nothing the workers race (the port reads AsNoTracking). This single-threaded scope is ALSO
        // where every distinct destination Folder row is resolved/created ONCE — folder creation is
        // shared mutable DB state, so it must never run inside the parallel PHASE B. We collect the
        // resolved TargetFolderPath → folderId map here and hand it to each worker's executor.
        var acting = new List<BatchUnit>();
        var folderIdByPath = new Dictionary<string, int>(DestinationResolver.SourcePathComparer);

        // PHASE A reports no progress percentage (that starts in PHASE B), so trace the planning loop to
        // the log — otherwise a large library sits at 0% here with no signal that it is still planning.
        LogPlanningStarted(runId, kind, ids.Length);

        await using (var readScope = ScopeFactory.CreateAsyncScope())
        {
            var readDb = readScope.ServiceProvider.GetRequiredService<DbContext>();
            var port = new CoveRenamerDataPort(readDb);
            var planner = new RenamerPlanner(port);

            int planIndex = 0;
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                var plan = await planner.PlanAsync(kind, id, options, lookups, ct);

                // File sizes for the free-space sum live on the loaded entity's files, not on the plan
                // item. Load the entity once and read each file's bytes by id.
                var entity = await port.LoadEntityAsync(kind, id, ct);
                var sizeByFileId = entity?.Files.ToDictionary(f => f.FileId, f => f.SizeBytes) ?? [];

                int actingThisItem = 0;
                foreach (var item in plan.Items)
                {
                    if (item.Status is not (RenamerStatus.Renamer or RenamerStatus.Move))
                    {
                        continue;
                    }

                    actingThisItem++;
                    long size = sizeByFileId.GetValueOrDefault(item.FileId);
                    // Hand each worker a single-file plan so the executor acts on exactly this file
                    // (it reloads plan.EntityId and processes plan.Items); the parent entity id rides
                    // the unit for logging.
                    var unitPlan = new RenamerPlan(plan.EntityId, plan.Kind, [item]);
                    acting.Add(new BatchUnit(plan.EntityId, unitPlan,
                        (item.OldFullPath, item.NewFullPath, size)));
                }

                LogItemPlanned(runId, ++planIndex, ids.Length, id, actingThisItem);
                // Planning drives the FIRST half of the bar (0 -> PlanningProgressShare). ids.Length is
                // known here, so the fraction is exact; the message names the phase so the UI reads
                // "Planning 6769/8238" rather than a silent 0%. PHASE B's reporter continues from
                // PlanningProgressShare to 1.0, so the bar only ever advances.
                progress.Report(
                    (double)planIndex / ids.Length * PlanningProgressShare,
                    $"Planning {planIndex}/{ids.Length}...");
            }

            // Pre-create/resolve every DISTINCT destination folder ONCE, here, on the single
            // read/write scope (no concurrency). A Move item's destination Folder row therefore EXISTS
            // before any parallel worker runs, and each worker reads its id from this map instead of
            // doing a check-then-act create on a shared row. An in-place Renamer uses the source folder
            // id (no entry needed). This is the single source of folder creation for the batch.
            foreach (var unit in acting)
            {
                var planItem = unit.Plan.Items[0];
                if (planItem.Status != RenamerStatus.Move)
                {
                    continue;
                }

                if (!folderIdByPath.ContainsKey(planItem.TargetFolderPath))
                {
                    folderIdByPath[planItem.TargetFolderPath] =
                        await port.GetOrCreateFolderIdAsync(planItem.TargetFolderPath, ct);
                }
            }
        }

        // UP-FRONT free-space refusal: sum the projected cross-volume bytes per destination volume and
        // refuse the whole batch before touching disk if a volume would not fit. Same-volume moves are
        // excluded from the sum by the guard. This runs BEFORE BeginBatchAsync, so a refused batch
        // opens no RevertLog header (and can never shadow a prior replayable batch).
        var moves = acting.Select(u => u.Move).ToList();
        var shortfall = FreeSpaceGuard.Shortfall(moves, options.FreeSpaceHeadroomBytes, freeSpaceProbe);
        if (shortfall.Count > 0)
        {
            string detail = string.Join("; ",
                shortfall.Select(s => $"{s.Volume}: need {s.Needed} bytes, {s.Available} free"));
            LogBatchDone(runId, 0, 0, 0);
            progress.Report(1d, $"Refused: insufficient free space ({detail}).");
            return;
        }

        // Nothing acts → open NO batch header (an empty open header would shadow the previous
        // replayable batch from /undo). Report the final 1.0 and return as a clean no-op.
        if (acting.Count == 0)
        {
            LogBatchDone(runId, 0, 0, 0);
            progress.Report(1d, "Nothing to renamer.");
            return;
        }

        // Now — and only now — open exactly one batch header: PHASE A produced acting work and the
        // batch fits. A later ReadLastOpenBatchAsync returns the whole run as one batch with its kind,
        // which /undo replays. The header is written ONCE here, single-threaded, never per worker.
        await revertLog.BeginBatchAsync(runId, kind, ct);

        // Marks the PHASE A → PHASE B boundary in the log: PHASE B's percentage now advances per
        // completed file, so a later stall is legible as "stuck partway through {Acting}", not silence.
        LogPlanningDone(runId, acting.Count, ids.Length);

        // ── PHASE B — execute, partitioned + bounded, per-worker scope ─────────────────────────────
        // Map a move back to its unit by the source full path so each partition group hands the worker
        // the right single-file plan. A duplicate OldFullPath (e.g. two Folder rows sharing one Path,
        // or two entities resolving to a colliding old path) would make ToDictionary throw an
        // ArgumentException AFTER the header is open — aborting the whole batch and masking the prior
        // undoable batch. Build it defensively (group + keep-first) so a duplicate source path is a
        // tolerated anomaly, not an unhandled throw; the duplicate's move tuple still points at the same
        // on-disk file, so the kept unit covers it.
        var unitByOldPath = acting
            .GroupBy(u => u.Move.OldFullPath)
            .ToDictionary(g => g.Key, g => g.First());
        var partitions = FreeSpaceGuard.PartitionByPair(moves);

        // Serialize every concurrent progress.Report. The PHASE B workers call progress.Report
        // from many threads at once (same-volume runs unbounded), and nothing establishes that the
        // host's IJobProgress sink is thread-safe — a host that appends to a list or writes a SignalR
        // message without its own lock could corrupt state or interleave messages under concurrency.
        // Guard the call with a lightweight lock so reports are mutually exclusive. The `done` counter
        // is already Interlocked; this only serializes the host-facing Report invocation itself.
        var progressGate = new object();

        int totalRenamed = 0, totalSkipped = 0, totalFailed = 0;
        int done = 0;
        int totalUnits = Math.Max(acting.Count, 1);

        async ValueTask RunUnitAsync(BatchUnit unit, CancellationToken token)
        {
            // Cross-volume only: re-check free space just before the copy so a concurrent scanner that
            // shrank the destination since PHASE A skips this item gracefully instead of filling the
            // disk. Same-volume moves consume ~no space and are excluded by the guard, so this is a
            // no-op for them.
            var inFlight = FreeSpaceGuard.Shortfall([unit.Move], options.FreeSpaceHeadroomBytes, freeSpaceProbe);
            if (inFlight.Count > 0)
            {
                Interlocked.Increment(ref totalSkipped);
                // A free-space refusal is neither a lock nor a collision — use the dedicated
                // SkipNoSpace status so log/monitor output attributes a disk-full skip correctly.
                LogItemSkipped(runId, kind, unit.EntityId, RenamerStatus.SkipNoSpace,
                    "skipped: destination volume dropped below free-space headroom in flight");
                Interlocked.Increment(ref done);
                ReportProgress((double)Volatile.Read(ref done) / totalUnits);
                return;
            }

            // OWN scope per worker → OWN DbContext → OWN port + executor. The shared revertLog is
            // passed in (its appends are serialized). The executor classifies-not-throws, so a per-item
            // fault is a skip/failure recorded below — only a genuine cancellation propagates. The
            // pre-resolved folderIdByPath is handed in so the executor reads each Move's destination
            // folder id from the map instead of doing a check-then-act create on a shared Folder row.
            // Log the move ABOUT to run: a cross-volume copy of a large file can take many seconds, and
            // PHASE B only logged COMPLETIONS — so a long gap read as a freeze. This "starting" line makes
            // an in-flight copy visible. crossVolume/size come from the already-known move tuple (no extra IO).
            bool crossVolume = !VolumeClassifier.SameVolume(unit.Move.OldFullPath, unit.Move.NewFullPath);
            long sizeMb = unit.Move.SizeBytes / (1024 * 1024);
            int doneNow = Volatile.Read(ref done);
            LogItemStarting(runId, doneNow, totalUnits, kind, unit.EntityId,
                crossVolume, sizeMb, unit.Move.OldFullPath);

            await using var scope = ScopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DbContext>();
            var exec = new RenamerExecutor(new CoveRenamerDataPort(db), EventBus, revertLog, new DiskMover());

            var result = await exec.ExecuteAsync(unit.Plan, options, folderIdByPath, token);
            LogBatchItem(runId, kind, unit.EntityId, result);

            // Thread-safe tally: a racing `+=` would lose increments under parallel workers.
            Interlocked.Add(ref totalRenamed, result.Renamed.Count);
            Interlocked.Add(ref totalSkipped, result.Skipped.Count);
            Interlocked.Add(ref totalFailed, result.Failed.Count);

            Interlocked.Increment(ref done);
            ReportProgress((double)Volatile.Read(ref done) / totalUnits);
        }

        // The single serialized entry point for every concurrent progress report (see the
        // progressGate comment above). Holding the gate makes the host-facing Report call mutually
        // exclusive across PHASE B workers.
        void ReportProgress(double percent)
        {
            // PHASE B owns the SECOND half of the bar: map its own [0,1] completion fraction into
            // [PlanningProgressShare, 1.0] so it picks up exactly where planning left off and never jumps
            // backwards. A same-message-less report keeps the host's own phase label; the final 1.0 after
            // the loop lands at exactly 1.0.
            double scaled = PlanningProgressShare + percent * (1d - PlanningProgressShare);
            lock (progressGate)
            {
                progress.Report(scaled, null);
            }
        }

        foreach (var (pair, pairMoves) in partitions)
        {
            ct.ThrowIfCancellationRequested();

            // Same-volume group is bounded by SameVolumeConcurrency (a pressure bound, not a space
            // guard — same-drive moves are instant metadata renames). A value <= 0 means unbounded
            // (legacy behavior), mapped to Parallel's -1 sentinel. Each cross-volume (src,dst) pair is
            // bounded by the configured per-pair concurrency.
            int degree = pair == FreeSpaceGuard.SameVolumePair
                ? (options.SameVolumeConcurrency > 0 ? options.SameVolumeConcurrency : -1)
                : options.CrossVolumeConcurrency;
            var units = pairMoves.Select(m => unitByOldPath[m.OldFullPath]).ToList();

            await Parallel.ForEachAsync(units,
                new ParallelOptions { MaxDegreeOfParallelism = degree, CancellationToken = ct },
                RunUnitAsync);
        }

        LogBatchDone(runId, totalRenamed, totalSkipped, totalFailed);
        progress.Report(1d, "Renamer complete.");
    }

    /// <summary>
    /// A bound on a single source-path regex match, applied at build time so a catastrophic-backtracking
    /// (ReDoS) pattern is interrupted instead of hanging the batch. Small because source-path matching
    /// is a short, per-entity string test, not a document scan.
    /// </summary>
    private static readonly TimeSpan RouteRegexMatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Builds the per-batch <see cref="RouteLookups"/> ONCE: the studio-id and exact-source-path
    /// dictionaries pass through; the tag map is rebuilt with <see cref="StringComparer.OrdinalIgnoreCase"/>;
    /// each <see cref="PathDestinationRule.IsRegex"/> rule is PRE-PARSED here with a bounded match
    /// timeout (NOT <c>RegexOptions.Compiled</c> — overkill for a short batch). An invalid user pattern
    /// is caught at THIS build step and skipped-with-a-log (classify, don't throw at the batch
    /// boundary) so it never throws mid-match; the resolver only ever calls <c>IsMatch</c>.
    /// </summary>
    private RouteLookups BuildLookups(RenamerOptions o)
    {
        // Exact source-path match must mirror the rest of the codebase's OS-aware path
        // semantics (VolumeClassifier / PathConfinement.IsUnderRoot use OrdinalIgnoreCase on Windows),
        // so a Windows user's exact rule for "media/incoming" matches a stored "Media/Incoming". Build
        // the map with the OS-aware comparer and NORMALIZE keys (trim a trailing '/') so a rule for
        // "media/incoming" also matches a stored "media/incoming/"; the resolver normalizes the source
        // path the same way before lookup.
        var exact = new Dictionary<string, string>(DestinationResolver.SourcePathComparer);
        var regexRules = new List<(Regex Pattern, string Dest)>();

        foreach (var rule in o.PathDestinations)
        {
            if (!rule.IsRegex)
            {
                // Exact source-path rule: first wins on a duplicate key (user order preserved).
                exact.TryAdd(DestinationResolver.NormalizeSourcePath(rule.Pattern), rule.Dest);
                continue;
            }

            try
            {
                regexRules.Add((new Regex(rule.Pattern, RegexOptions.None, RouteRegexMatchTimeout), rule.Dest));
            }
            catch (ArgumentException ex)
            {
                // An invalid user regex is skipped (not the whole batch) with a clear logged reason —
                // parse-time, never match-time.
                LogInvalidRouteRegex(rule.Pattern, ex.Message);
            }
        }

        // Pre-parse the exclude lookups ONCE beside the routing sets. The exact tag-name set is
        // case-insensitive (mirroring tag routing); the exact path set uses the same
        // OS-aware comparer + NormalizeSourcePath keys as the routing exact map; each exclude regex is
        // compiled ONCE with the SAME RouteRegexMatchTimeout and the SAME classify-not-throw shape, so
        // an invalid exclude pattern is skipped-with-a-log at build time and never aborts the batch.
        var excludeTags = new HashSet<string>(o.ExcludeTags, StringComparer.OrdinalIgnoreCase);
        var excludeStudios = new HashSet<int>(o.ExcludeStudioIds);
        var excludePathsExact = new HashSet<string>(DestinationResolver.SourcePathComparer);
        var excludePathRegex = new List<Regex>();

        foreach (var rule in o.ExcludePaths)
        {
            if (!rule.IsRegex)
            {
                excludePathsExact.Add(DestinationResolver.NormalizeSourcePath(rule.Pattern));
                continue;
            }

            try
            {
                excludePathRegex.Add(new Regex(rule.Pattern, RegexOptions.None, RouteRegexMatchTimeout));
            }
            catch (ArgumentException ex)
            {
                // Same parse-time, never match-time skip-with-a-log as the routing regex.
                LogInvalidRouteRegex(rule.Pattern, ex.Message);
            }
        }

        return new RouteLookups(
            o.StudioDestinations,
            new Dictionary<string, string>(o.TagDestinations, StringComparer.OrdinalIgnoreCase),
            exact,
            regexRules,
            excludeTags,
            excludeStudios,
            excludePathsExact,
            excludePathRegex);
    }

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
