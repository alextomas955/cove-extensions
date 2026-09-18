using System.Text.RegularExpressions;
using Cove.Core.Events;
using Cove.Extensions.Shared;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Renamer.Execution;
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

}
