using System.Text.RegularExpressions;
using Cove.Core.Events;
using Cove.Extensions.Shared;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Renamer.Execution;
using Renamer.Jobs;
using Renamer.Options;
using Renamer.Planner;

using CoveConfiguration = Cove.Core.Interfaces.CoveConfiguration;

namespace Renamer;

public sealed partial class Renamer : FullExtensionBase
{
    // Identity and metadata come from extension.json, which the host applies to this instance
    // (IManifestAware.ApplyManifest) before it reads any of them. The host reads each value straight
    // off the property, so an override declared here overrides the manifest silently.

    // The executor needs a SCOPED CoveContext per run (a DbContext is scoped, not singleton) and the
    // host IEventBus for the post-renamer reindex event. Capture the scope factory + event bus
    // in InitializeAsync; a run opens its own scope via CreateAsyncScope() and resolves the scoped
    // DbContext there.

    // Resolved once at load and never null afterwards. The fields are nullable only because they are
    // assigned in InitializeAsync rather than the ctor; every use site is reached only after init, so
    // the non-null accessors below are the single, guarded way the rest of the extension reads them.
    private IServiceScopeFactory? _scopeFactory;
    private IEventBus? _eventBus;
    private CoveConfiguration? _coveConfig;

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

    /// <summary>Cove's configured library paths, the list every destination root is chosen from.</summary>
    private IReadOnlyList<string> LibraryRoots => CoveRenamerDataPort.ReadLibraryRoots(_coveConfig);

    public override async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
    {
        // Resolve the captured seams with GetRequiredService so a missing host registration fails
        // clearly here, at load, instead of surfacing as a NullReferenceException at first use.
        _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        _eventBus = services.GetRequiredService<IEventBus>();
        // Logging is optional: the host forwards ILogger into the extension scope, but treat its
        // absence as non-fatal (GetService, not GetRequiredService) — a renamer must still run. Keep the
        // NullLogger default when the host supplies none.
        _log = services.GetService<ILogger<Renamer>>() ?? _log;
        // Optional for the same reason logging is: a host that registers no configuration must still
        // load the extension. The cost is visible rather than silent - with no library paths, an item
        // with a folder template plans as SkipUnanchored and says so.
        _coveConfig = services.GetService<CoveConfiguration>();
        if (_coveConfig is null)
        {
            LogNoCoveConfiguration();
        }

        // The first thing this method does with the database, and it stays first: the host has
        // already had its chance to apply this extension's schema migration on every load path
        // (boot, runtime install, enable), so by here the journal either exists or never will.
        await AssertJournalIsReachableAsync(ct);

        // Deleted UNCONDITIONALLY, and never read. A pre-0.2.1 whole-library scan wrote one wire row per
        // file to this key, so on a large library its value reaches hundreds of megabytes; Cove's bulk
        // extension-data read serializes every value an extension owns into one response, so a single
        // oversized value makes EVERY settings read for this extension fail — including any read that
        // could measure it. The host exposes no per-key size probe, and its delete materializes the row it
        // removes, so a conditional purge would have to load the very string that cannot be loaded. Hence:
        // no read, no condition, and the replacement aggregate lives under a different key.
        try
        {
            await Store.DeleteAsync(LastScanResultKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort recovery: a load that refuses to complete because the cleanup failed leaves the
            // user worse off than the oversized value did. Reported once, then continue. Cancellation is
            // NOT a purge failure, so it is left to propagate.
            LogLegacyScanPurgeFailed(ex);
        }

        // ONE-TIME journal migration: an installation upgrading into the table-backed journal still
        // carries its undo under the two legacy store keys, so a code change alone would silently throw
        // that undo away. This moves it into the table and then deletes both keys — which also stops an
        // oversized leftover being served by the bulk extension-data read, with no SQL.
        //
        // Safe HERE, after the assertion above: the host applies this extension's schema migration
        // BEFORE InitializeAsync on all three lifecycle paths (boot, runtime install, enable), so the
        // table already exists. There is no marker to check first — deleting the source keys IS the
        // marker, so a second load finds nothing and does nothing.
        try
        {
            await MigrateStoredJournalAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reported and stepped over rather than blocking the load: an install that refuses to come
            // up because a legacy cleanup failed leaves the user worse off than the leftover did.
            LogJournalBlobMigrationFailed(ex);
        }

        // ONE-TIME options conversion: a stored blob keyed by tag/performer NAME does not bind to the
        // current model, and the options store answers a bind failure with defaults, so leaving it
        // unconverted presents the user an empty settings panel and renames nothing they configured.
        //
        // Deliberately AFTER the journal work and guarded the same way: a conversion that cannot run
        // yet must not stop the extension loading, because every path that could fix it is behind a
        // panel this extension serves.
        try
        {
            await MigrateStoredOptionsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOptionsMigrationFailed(ex);
        }

        await base.InitializeAsync(services, ct);
    }

    /// <summary>
    /// Rewrites the stored options blob into the shape the current model declares - name-keyed
    /// entity rules to stable ids, and typed destination roots to a Cove library path plus a relative
    /// template - exactly once.
    /// </summary>
    /// <remarks>
    /// Defers rather than converts when a name still needing resolution belongs to an entity table
    /// holding no rows at all: that is the shape of a library the extension cannot read yet, and
    /// converting against it would resolve every name to nothing and write the user's whole rule set
    /// away. Deferring costs one more pass on the next load; converting early is unrecoverable, because
    /// the names are gone from the blob afterwards.
    /// <para>
    /// The stamp is written on the no-work path too, so an install with nothing to convert stops
    /// re-scanning its blob on every load.
    /// </para>
    /// </remarks>
    private async Task MigrateStoredOptionsAsync(CancellationToken ct)
    {
        if (await Store.GetAsync(OptionsMigration.SchemaKey, ct) == OptionsMigration.CurrentSchema)
        {
            return;
        }

        var stored = await Store.GetAsync(OptionsStore.Key, ct);
        if (string.IsNullOrWhiteSpace(stored))
        {
            // An install that has never saved options has nothing to convert and nothing to stamp:
            // loading writes no store key at all, and re-reaching this point costs one absent read.
            return;
        }

        bool rewrote = false;
        var legacy = OptionsMigration.Scan(stored);
        if (legacy.Any)
        {
            await using var scope = ScopeFactory.CreateAsyncScope();
            var resolved = await RunAsSystem.RunAsSystemAsync(scope.ServiceProvider, async () =>
            {
                var port = new CoveRenamerDataPort(scope.ServiceProvider.GetRequiredService<DbContext>());
                var tags = await port.ResolveNamesAsync(RenamerEntityKind.Tag, legacy.Tags, ct);
                var performers = await port.ResolveNamesAsync(RenamerEntityKind.Performer, legacy.Performers, ct);
                return (tags, performers);
            });

            if ((legacy.Tags.Count > 0 && !resolved.tags.TableHasRows)
                || (legacy.Performers.Count > 0 && !resolved.performers.TableHasRows))
            {
                LogOptionsMigrationDeferred(legacy.Tags.Count, legacy.Performers.Count);
                return;
            }

            var conversion = OptionsMigration.Convert(
                stored, resolved.tags.Matches, resolved.performers.Matches);
            stored = conversion.Json;
            rewrote = true;

            foreach (var name in conversion.DroppedNames)
            {
                LogOptionsRuleDropped(name);
            }

            foreach (var collapse in conversion.CaseCollapses)
            {
                LogOptionsRuleCaseCollapsed(collapse.Name, collapse.MatchedId, collapse.AlsoMatchedIds.Count);
            }

            foreach (var discard in conversion.DiscardedDestinations)
            {
                LogOptionsDestinationDiscarded(discard.Key, discard.Id, discard.ClaimedBy);
            }
        }

        var destinations = OptionsMigration.ConvertDestinationsToRoots(stored, LibraryRoots);
        if (destinations.Deferred)
        {
            // Deferred, and deliberately unstamped, for the same reason the half above defers: a
            // destination can only be placed under a library path Cove supplies, and an empty list is
            // indistinguishable from a host that has not supplied one yet. Converting anyway would
            // DROP every rule the user has.
            LogOptionsDestinationMigrationDeferred();
            return;
        }

        if (destinations.Changed)
        {
            stored = destinations.Json;
            rewrote = true;

            foreach (var rule in destinations.Rewritten)
            {
                LogOptionsDestinationRewritten(rule.Rule, rule.From, rule.ToRoot, rule.ToTemplate);
            }

            foreach (var rule in destinations.Dropped)
            {
                LogOptionsDestinationDropped(rule.Rule, rule.Stored);
            }
        }

        if (rewrote)
        {
            await Store.SetAsync(OptionsStore.Key, stored, ct);
        }

        await Store.SetAsync(OptionsMigration.SchemaKey, OptionsMigration.CurrentSchema, ct);
    }

    /// <summary>The journal table whose absence must stop this extension loading.</summary>
    private const string JournalBatchTable = "renamer_revert_batches";

    /// <summary>Refuses to load when the undo journal cannot be read.</summary>
    /// <remarks>
    /// Why the extension checks this itself rather than leaving it to the host: the host applies an
    /// extension's migrations, and on a failure it logs, stops applying, and loads the extension
    /// anyway. So a migration that never landed is one log line, after which every rename would move
    /// a file with no record of where it came from — a loss of undo that looks exactly like a working
    /// install. A throw out of this method is the opposite kind of failure: the host catches it per
    /// extension and disables THIS extension while the rest of it keeps running, which nobody can
    /// mistake for success.
    /// <para>
    /// It deliberately does not create the table. The host owns applying and receipting migrations,
    /// and a bootstrap here would write no receipt and duplicate the retry semantics the host already
    /// implements.
    /// </para>
    /// </remarks>
    private async Task AssertJournalIsReachableAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();

            // A filtered read that returns nothing is the failure mode this whole check exists to make
            // impossible to mistake for success, so the scope must be the elevated one.
            await RunAsSystem.RunAsSystemAsync(scope.ServiceProvider, () =>
            {
                var db = scope.ServiceProvider.GetRequiredService<DbContext>();
                return db.Set<RevertBatchEntity>().AsNoTracking().AnyAsync(ct);
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogJournalUnreachable(ex, JournalBatchTable);
            throw new InvalidOperationException(
                $"The undo journal table '{JournalBatchTable}' could not be read, so a rename would "
                    + "move files with no record of where they came from and no way to undo it. The "
                    + "extension refuses to load rather than rename unjournalled.",
                ex);
        }
    }

    /// <summary>Moves the legacy stored journal into the journal table exactly once, then clears it.</summary>
    private async Task MigrateStoredJournalAsync(CancellationToken ct)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        int moved = await RunAsSystem.RunAsSystemAsync(scope.ServiceProvider, async () =>
        {
            var db = scope.ServiceProvider.GetRequiredService<DbContext>();
            using var journal = new CoveRevertJournal(db);
            return await JournalBlobMigration.RunAsync(Store, journal, DateTime.UtcNow, ct);
        });

        if (moved > 0)
        {
            LogJournalBlobMigrated(moved);
        }
    }

    // The SINGLE method the job runner and the /renamer enqueued delegate both
    // call. A THIN adapter over the planner+executor: decode → scope → loop → report. No
    // renamer/move/collision/rollback logic lives here.

    /// <summary>
    /// Maps a Cove entity-type string to a <see cref="RenamerFileKind"/>. Supports
    /// <c>video</c>/<c>image</c>/<c>audio</c>/<c>text</c> (case-insensitive); everything
    /// else — including <c>gallery</c> (unsupported) and unknown — returns false with the
    /// kind defaulted. NOT <c>Enum.Parse</c>: Cove's type strings do not map 1:1 to the enum names.
    /// </summary>
    /// <remarks>
    /// The plural spellings are accepted because the host singularizes only two of its own: its
    /// selection-action normalizer rewrites <c>videos</c> and <c>images</c> and passes every other
    /// list's entity type through unchanged, so a bulk action on the texts or audios list arrives here
    /// as <c>texts</c>/<c>audios</c>.
    /// </remarks>
    internal static bool TryParseKind(string? entityType, out RenamerFileKind kind)
    {
        switch (entityType?.ToLowerInvariant())
        {
            case "video" or "videos": kind = RenamerFileKind.Video; return true;
            case "image" or "images": kind = RenamerFileKind.Image; return true;
            case "audio" or "audios": kind = RenamerFileKind.Audio; return true;
            case "text" or "texts": kind = RenamerFileKind.Text; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>
    /// Maps a <see cref="RenamerFileKind"/> to the host read/write permission pair that gates operating
    /// on that entity kind. Cove models entity permissions per-kind (<c>videos.*</c>/<c>images.*</c>/
    /// <c>audios.*</c>/<c>texts.*</c>), so a renamer of an image must require <c>images.write</c>, not
    /// the video permission.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not one this extension renames.</exception>
    internal static (string Read, string Write) PermissionsFor(RenamerFileKind kind) => kind switch
    {
        RenamerFileKind.Image => (Cove.Core.Auth.Permissions.ImagesRead, Cove.Core.Auth.Permissions.ImagesWrite),
        RenamerFileKind.Audio => (Cove.Core.Auth.Permissions.AudiosRead, Cove.Core.Auth.Permissions.AudiosWrite),
        RenamerFileKind.Text => (Cove.Core.Auth.Permissions.TextsRead, Cove.Core.Auth.Permissions.TextsWrite),
        RenamerFileKind.Video => (Cove.Core.Auth.Permissions.VideosRead, Cove.Core.Auth.Permissions.VideosWrite),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a renamable kind"),
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
    /// One acting file's unit of execution work: a single-file plan the worker hands the executor, the
    /// projected move tuple (used to partition same- from cross-volume and to re-check free space in
    /// flight), and the parent entity id for per-item logging.
    /// </summary>
    private readonly record struct BatchUnit(
        int EntityId,
        Planner.RenamerPlan Plan,
        (string OldFullPath, string NewFullPath, long SizeBytes) Move);

    /// <summary>
    /// The fraction of a chunk's progress bar the planning pass owns, the rest belonging to execution.
    /// Planning a full chunk is slow, so it needs a share of the bar to report into. The exact split is
    /// cosmetic: both passes scale linearly, so the bar only ever advances.
    /// </summary>
    private const double PlanningProgressShare = 0.5;

    /// <summary>The entities a rename run plans and executes before it starts the next chunk.</summary>
    /// <remarks>
    /// Equal to <see cref="MaxEntityIdsPerRequest"/>, so one selection is always one chunk. Nothing a
    /// run holds grows past a chunk — its plans, projected moves and destination-folder map are
    /// released when the chunk ends — so a whole-library run costs what one full selection costs.
    /// </remarks>
    internal const int RenameChunkEntities = MaxEntityIdsPerRequest;

    /// <summary>What one chunk did, and the free-space refusal that stops the run when it is set.</summary>
    private readonly record struct ChunkOutcome(
        int Renamed, int Skipped, int Failed, int ContestedFiles, string? Shortfall);

    /// <summary>The acting files one operation has offered the journal, and the latch its cap trips.</summary>
    /// <remarks>
    /// The cap bounds a user action, not a chunk: measured per chunk, a whole-library run would never
    /// reach it and the undo it protects would be the partial record the cap exists to refuse. Once
    /// tripped it stays tripped for the rest of the operation, so a later chunk cannot journal the tail
    /// of a run whose head has already been dropped.
    /// </remarks>
    internal sealed class OperationJournalBudget(string operationId)
    {
        private int _actingFiles;

        public string OperationId { get; } = operationId;

        public bool Suppressed { get; private set; }

        /// <summary>Adds a chunk's acting files and returns the operation's running total.</summary>
        public int Add(int actingFiles) => _actingFiles += actingFiles;

        public void Suppress() => Suppressed = true;
    }

    /// <summary>Maps one chunk's own progress onto its share of a run over <paramref name="total"/> entities.</summary>
    private sealed class ChunkSliceProgress(IJobProgress inner, int offset, int share, int total) : IJobProgress
    {
        public void Report(double percent, string? message = null)
            => inner.Report(
                Math.Clamp((offset + (Math.Clamp(percent, 0d, 1d) * share)) / Math.Max(total, 1), 0d, 1d),
                message);
    }

    /// <summary>The free-space reading the up-front refusal and the in-flight re-check share.</summary>
    /// <remarks>
    /// An unprobeable volume reads as <see cref="long.MaxValue"/> and never blocks a run: the reading is
    /// a pre-flight courtesy, and the cross-volume mover verifies every copy and fails each item safely
    /// when the disk really is full. <c>DriveInfo</c> throws for a root that is not a drive letter, such
    /// as a UNC share reached through an allowed root, and the reading itself can hit a transient IO
    /// error on an offline volume.
    /// </remarks>
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

    /// <summary>Renames every id in the decoded batch and reports a final <c>1.0</c>.</summary>
    /// <remarks>
    /// One selection is one user action, so this call is its own operation and everything it renames
    /// comes back from a single undo. Bad, empty or unsupported input is a clean no-op that still
    /// reports the final <c>1.0</c>: job parameters are untrusted, and this never throws on them.
    /// </remarks>
    /// <param name="parameters">The host's string-only job parameter map (entity type plus id list).</param>
    /// <param name="progress">The job-progress sink, reported through the run and a final <c>1.0</c>.</param>
    /// <param name="ct">Cancellation token; a genuine cancellation aborts the run.</param>
    /// <param name="freeSpaceProbe">
    /// The available-free-space reading both the up-front refusal and the in-flight re-check take;
    /// defaults to <see cref="AvailableFreeSpace"/>.
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

        var options = await new OptionsStore(Store, _log).LoadAsync(ct);

        int taken = 0;
        Task<IReadOnlyList<int>> NextChunk(CancellationToken token)
        {
            IReadOnlyList<int> chunk = [.. ids.Skip(taken).Take(RenameChunkEntities)];
            taken += chunk.Count;
            return Task.FromResult(chunk);
        }

        await RunRenameChunksAsync(
            kind, NextChunk, ids.Length, new OperationJournalBudget(Guid.NewGuid().ToString("N")),
            options, freeSpaceProbe ?? AvailableFreeSpace, RenameChunkEntities, progress, ct);
    }

    /// <summary>
    /// Renames every entity of <paramref name="kind"/>, walking the kind's ids a page at a time through
    /// the same chunk the selection path drives.
    /// </summary>
    /// <remarks>
    /// <paramref name="totalEntities"/> is a snapshot taken before the walk, so the fraction it scales
    /// can drift from what the pages yield; the caller's own final report is what lands the bar. The
    /// walk's cursor is the entity id, which a rename never changes, so the run's own writes can
    /// neither skip a page nor repeat one.
    /// </remarks>
    /// <param name="kind">The media kind to walk.</param>
    /// <param name="totalEntities">The progress denominator, counted before the walk starts.</param>
    /// <param name="budget">The journal budget of the operation this walk belongs to.</param>
    /// <param name="options">The options the whole run plans with, read once by the caller.</param>
    /// <param name="progress">The sink this kind's share of the run reports into.</param>
    /// <param name="ct">Cancellation token; a cancellation between chunks leaves earlier chunks done and undoable.</param>
    /// <param name="freeSpaceProbe">The free-space reading; defaults to <see cref="AvailableFreeSpace"/>.</param>
    /// <param name="chunkEntities">The entities one chunk covers.</param>
    internal Task RunRenamerKindAsync(
        RenamerFileKind kind, int totalEntities, OperationJournalBudget budget, RenamerOptions options,
        IJobProgress progress, CancellationToken ct, Func<string, long>? freeSpaceProbe = null,
        int chunkEntities = RenameChunkEntities)
    {
        int after = 0;
        async Task<IReadOnlyList<int>> NextPageAsync(CancellationToken token)
        {
            var page = await RunAsSystem.RunInSystemScopeAsync(
                ScopeFactory,
                services => new CoveRenamerDataPort(services.GetRequiredService<DbContext>(), _coveConfig)
                    .LoadEntityIdPageAsync(kind, after, chunkEntities, token));

            if (page.Count > 0)
            {
                after = page[^1];
            }

            return page;
        }

        return RunRenameChunksAsync(
            kind, NextPageAsync, totalEntities, budget, options,
            freeSpaceProbe ?? AvailableFreeSpace, chunkEntities, progress, ct);
    }

    /// <summary>
    /// Drives <paramref name="nextChunk"/> to exhaustion through the shared chunk body, tallies what
    /// the chunks did and reports the run's final <c>1.0</c> with what happened.
    /// </summary>
    private async Task RunRenameChunksAsync(
        RenamerFileKind kind,
        Func<CancellationToken, Task<IReadOnlyList<int>>> nextChunk,
        int totalEntities,
        OperationJournalBudget budget,
        RenamerOptions options,
        Func<string, long> freeSpaceProbe,
        int chunkEntities,
        IJobProgress progress,
        CancellationToken ct)
    {
        // Hoisted once for the whole run: the studio-id, tag-name and exact-path dictionaries and the
        // pre-parsed source-path regex set, so the resolver never re-walks or re-compiles them per
        // entity. An invalid user regex is caught at this build step and skipped with a log, so it can
        // never throw mid-match.
        var lookups = BuildLookups(options);

        // The journal gets its own scope, and therefore its own DbContext, for the whole run: every
        // parallel worker of every chunk shares it because it mints each row's sequence number, and a
        // DbContext is not thread-safe, so it cannot ride on a worker's scope. Its writes need no
        // elevation because its two tables are extension-owned and carry none of CoveContext's
        // per-principal query filters.
        await using var journalScope = ScopeFactory.CreateAsyncScope();
        using var journal = new CoveRevertJournal(journalScope.ServiceProvider.GetRequiredService<DbContext>());

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
                chunk, kind, options, lookups, budget, journal, freeSpaceProbe,
                new ChunkSliceProgress(progress, entitiesDone, chunk.Count, totalEntities), ct);

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

            if (chunk.Count < chunkEntities)
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
            return;
        }

        if (renamed == 0 && failed == 0 && skipped == contested)
        {
            progress.Report(1d, $"Nothing to renamer.{RefusedNote(contested)}");
            return;
        }

        progress.Report(1d, $"Rename complete.{RefusedNote(contested)}");
    }

    /// <summary>
    /// Plans one chunk of ids over a single read-only scope, refuses it if a destination volume would
    /// not fit, then executes what acts in parallel.
    /// </summary>
    /// <remarks>
    /// The one implementation both a selection and a whole-library walk run through. Same-volume
    /// renames run bounded by <c>SameVolumeConcurrency</c> and cross-volume copies by
    /// <c>CrossVolumeConcurrency</c> within one (source,destination) disk pair; the pairs themselves run
    /// one after another, so peak concurrency is one pair's bound and never the sum over pairs. Each
    /// worker opens its own scope and resolves its own <see cref="DbContext"/>, because a
    /// <c>DbContext</c> is not thread-safe and Cove disables EF's thread-safety checks, so a shared one
    /// would corrupt silently.
    /// </remarks>
    private async Task<ChunkOutcome> RunRenameChunkAsync(
        IReadOnlyList<int> ids,
        RenamerFileKind kind,
        RenamerOptions options,
        RouteLookups lookups,
        OperationJournalBudget budget,
        IRevertJournal journal,
        Func<string, long> freeSpaceProbe,
        IJobProgress progress,
        CancellationToken ct)
    {
        // A fresh run id per chunk, and the operation id constant across the run. An undo acts on the
        // operation, so however many batches a run opens, the user has one action to reverse.
        var runId = Guid.NewGuid().ToString("N");

        LogBatchStarted(runId, kind, ids.Count);

        // Planning reads only. It is sequential for deterministic preview ordering, it mutates nothing
        // the workers race, and it writes nothing at all, so a chunk refused below leaves the database
        // as it found it.
        var planned = new List<BatchUnit>();

        // Planning reports no percentage of its own until the loop starts, so trace it to the log —
        // otherwise a large chunk sits at its opening percentage with no signal that it is still planning.
        LogPlanningStarted(runId, kind, ids.Count);

        // One elevated span for the whole planning pass, not one per entity: the background principal is
        // anonymous, and an unelevated read returns zero rows with no error.
        await RunAsSystem.RunInSystemScopeAsync(ScopeFactory, async services =>
        {
            var readDb = services.GetRequiredService<DbContext>();
            var port = new CoveRenamerDataPort(readDb, _coveConfig);
            var planner = new RenamerPlanner(port);

            int planIndex = 0;
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                var (plan, entity) = await planner.PlanWithEntityAsync(kind, id, options, lookups, ct);

                // File sizes for the free-space sum live on the loaded entity's files, not on the plan
                // item, so they are read off the entity the planner just loaded.
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
                    // Each worker is handed a single-file plan so the executor acts on exactly this file;
                    // the parent entity id rides the unit for logging.
                    var unitPlan = new RenamerPlan(plan.EntityId, plan.Kind, [item]);
                    planned.Add(new BatchUnit(plan.EntityId, unitPlan,
                        (item.OldFullPath, item.NewFullPath, size)));
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
            acting.Select(u => u.Move), options.FreeSpaceHeadroomBytes, freeSpaceProbe);
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

        // Now, and only now, open exactly one batch: the chunk produced acting work and it fits. The cap
        // is measured in files, so it takes acting.Count and not the entity count.
        await OpenOrSuppressBatchAsync(journal, runId, budget, kind, acting.Count, DateTime.UtcNow, ct);

        // Marks the planning/execution boundary in the log: the percentage now advances per completed
        // file, so a later stall is legible as "stuck partway through {Acting}", not as silence.
        LogPlanningDone(runId, acting.Count, ids.Count);

        // Partitioned, bounded, one scope per worker. The partitions carry the units themselves, so every
        // unit is scheduled exactly once whatever its paths are.
        var partitions = FreeSpaceGuard.PartitionByPair(
            acting, u => (u.Move.OldFullPath, u.Move.NewFullPath));

        // Serializes every concurrent progress report. The workers call Report from many threads at once,
        // and nothing establishes that the host's sink is thread-safe — a host that appends to a list or
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
            var inFlight = FreeSpaceGuard.Shortfall([unit.Move], options.FreeSpaceHeadroomBytes, freeSpaceProbe);
            if (inFlight.Count > 0)
            {
                Interlocked.Increment(ref totalSkipped);
                // A free-space refusal is neither a lock nor a collision, so it carries the dedicated
                // SkipNoSpace status and log output attributes a disk-full skip correctly.
                LogItemSkipped(runId, kind, unit.EntityId, RenamerStatus.SkipNoSpace,
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
            LogItemStarting(runId, doneNow, totalUnits, kind, unit.EntityId,
                crossVolume, sizeMb, unit.Move.OldFullPath);

            var result = await RunAsSystem.RunInSystemScopeAsync(ScopeFactory, services =>
            {
                var db = services.GetRequiredService<DbContext>();
                var exec = new RenamerExecutor(
                    new CoveRenamerDataPort(db, _coveConfig), EventBus, journal, runId, new DiskMover());
                return exec.ExecuteAsync(unit.Plan, options, folderIdByPath, token);
            });
            LogBatchItem(runId, kind, unit.EntityId, result);

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
            int sameVolumeDegree = options.SameVolumeConcurrency > 0 ? options.SameVolumeConcurrency : -1;
            int degree = pair == FreeSpaceGuard.SameVolumePair
                ? sameVolumeDegree
                : options.CrossVolumeConcurrency;
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
    /// Opens <paramref name="runId"/>'s journal batch, or suppresses journalling for the whole operation
    /// once its running acting-file total is past the row cap.
    /// </summary>
    /// <remarks>
    /// Suppressing takes the operation out rather than recording part of it: a partly-journalled rename
    /// reads exactly like a whole one, and the undo after it is quietly partial. A whole-library run over
    /// the cap is therefore not undoable at all, which the log says once per chunk that meets the latch.
    /// <para>
    /// Both the manual run and the per-edit auto-renamer decide this here, so a rename's undoability
    /// never depends on which path performed it. One method also gives the branch a seam a test can
    /// reach: the cap is thousands of files, so driving the suppressed side through either caller would
    /// mean seeding that many files on disk.
    /// </para>
    /// </remarks>
    internal async Task OpenOrSuppressBatchAsync(
        IRevertJournal journal,
        string runId,
        OperationJournalBudget budget,
        RenamerFileKind kind,
        int actingFiles,
        DateTime nowUtc,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(budget);

        int operationTotal = budget.Add(actingFiles);

        if (budget.Suppressed || IRevertJournal.ExceedsCap(operationTotal))
        {
            budget.Suppress();
            // Latches this journal instance and deletes what the operation already wrote. A run spanning
            // several kinds opens a journal per kind, so a later kind's instance has to be latched too.
            // The delete is scoped to the operation, so every other action's undo survives.
            await journal.SuppressAsync(budget.OperationId, ct);
            LogBatchNotJournalled(runId, operationTotal, IRevertJournal.MaxJournalledFiles);
            return;
        }

        await journal.BeginBatchAsync(runId, budget.OperationId, kind, nowUtc, ct);
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
        var exact = new Dictionary<string, Destination>(DestinationResolver.SourcePathComparer);
        var regexRules = new List<(Regex Pattern, Destination Dest)>();

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

        // Pre-parse the exclude lookups ONCE beside the routing sets. The exact path set uses the same
        // OS-aware comparer + NormalizeSourcePath keys as the routing exact map; each exclude regex is
        // compiled ONCE with the SAME RouteRegexMatchTimeout and the SAME classify-not-throw shape, so
        // an invalid exclude pattern is skipped-with-a-log at build time and never aborts the batch.
        var excludeTags = new HashSet<int>(o.ExcludeTagIds);
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
            o.TagDestinations,
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
