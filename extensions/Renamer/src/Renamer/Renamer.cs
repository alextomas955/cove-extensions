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
    // Identity and metadata come from extension.json, which the host applies through
    // IManifestAware.ApplyManifest before reading any of them. The host reads each value straight off
    // the property, so an override declared here overrides the manifest silently.

    // Assigned in InitializeAsync, not the constructor, hence nullable. The guarded accessors below
    // are the only way the rest of the extension reads them.
    private IServiceScopeFactory? _scopeFactory;
    private IEventBus? _eventBus;
    private CoveConfiguration? _coveConfig;

    // Defaults to a no-op logger and is replaced in InitializeAsync when the host supplies one, so
    // the source-generated [LoggerMessage] methods never dereference null and a missing host logger
    // never blocks a rename. The generator binds to this field by its ILogger type.
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
        // GetRequiredService so a missing host registration fails at load, not as a
        // NullReferenceException at first use.
        _scopeFactory = services.GetRequiredService<IServiceScopeFactory>();
        _eventBus = services.GetRequiredService<IEventBus>();
        // Logging is optional; a rename must still run without it.
        _log = services.GetService<ILogger<Renamer>>() ?? _log;
        // Also optional. With no library paths an item carrying a folder template plans as
        // SkipUnanchored and says so, so the cost is visible.
        _coveConfig = services.GetService<CoveConfiguration>();
        if (_coveConfig is null)
        {
            LogNoCoveConfiguration();
        }

        // Stays the first database touch: the host has already had its chance to apply this
        // extension's schema migration on every load path, so by here the journal exists or never
        // will.
        await AssertJournalIsReachableAsync(ct);

        // Deleted unconditionally and never read. An early whole-library scan wrote one wire row per
        // file to this key, reaching hundreds of megabytes on a large library, and Cove's bulk
        // extension-data read serializes every value an extension owns into one response, so one
        // oversized value fails every settings read this extension serves. The host exposes no
        // per-key size probe, so a conditional purge would have to load the string that cannot be
        // loaded. The replacement aggregate lives under a different key.
        try
        {
            await Store.DeleteAsync(LastScanResultKey, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A load that refuses to complete because the cleanup failed leaves the user worse off
            // than the oversized value does. Cancellation is not a purge failure and propagates.
            LogLegacyScanPurgeFailed(ex);
        }

        // An installation upgrading into the table-backed journal still carries its undo under two
        // legacy store keys, which a code change alone would discard silently. Safe after the
        // assertion above, since the host applies the schema migration before InitializeAsync on
        // every load path. Deleting the source keys is the marker, so a second load does nothing.
        try
        {
            await MigrateStoredJournalAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An install that refuses to come up because a legacy cleanup failed leaves the user
            // worse off than the leftover does.
            LogJournalBlobMigrationFailed(ex);
        }

        // A stored blob keyed by tag or performer name does not bind to the current model, and the
        // options store answers a bind failure with defaults, so leaving it unconverted shows an
        // empty settings panel and renames nothing the user configured. Guarded like the journal
        // work above: every path that could fix a failure is behind a panel this extension serves.
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

    // Rewrites the stored options blob into the current model's shape exactly once: name-keyed
    // entity rules become stable ids, and typed destination roots become a library path plus a
    // relative template.
    //
    // Defers when a name still needing resolution belongs to an entity table holding no rows: that is
    // a library the extension cannot read yet, and converting against it resolves every name to
    // nothing and writes the user's whole rule set away. Deferring costs one pass on the next load;
    // converting early is unrecoverable, because the names are gone from the blob afterwards. The
    // stamp is written on the no-work path too, so an install with nothing to convert stops
    // re-scanning its blob every load.
    private async Task MigrateStoredOptionsAsync(CancellationToken ct)
    {
        if (await Store.GetAsync(OptionsMigration.SchemaKey, ct) == OptionsMigration.CurrentSchema)
        {
            return;
        }

        var stored = await Store.GetAsync(OptionsStore.Key, ct);
        if (string.IsNullOrWhiteSpace(stored))
        {
            // An install that never saved options has nothing to convert and nothing to stamp.
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
            // Unstamped for the reason the half above defers: a destination can only be placed under
            // a library path Cove supplies, and an empty list cannot be told from a host that has not
            // supplied one yet. Converting anyway drops every rule the user has.
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

    private const string JournalBatchTable = "renamer_revert_batches";

    // Refuses to load when the undo journal cannot be read. The host logs a failed migration, stops
    // applying, and loads the extension anyway, so a migration that never landed would leave every
    // rename moving files with no record of where they came from, looking exactly like a working
    // install. A throw here makes the host disable this extension alone.
    //
    // Creates no table: the host owns applying and receipting migrations, and a bootstrap here would
    // write no receipt.
    private async Task AssertJournalIsReachableAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = ScopeFactory.CreateAsyncScope();

            // An unelevated read returns zero rows with no error, which is the failure this check
            // exists to catch.
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

    // Moves the legacy stored journal into the journal table exactly once, then clears it.
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

    // Maps a Cove entity-type string to a RenamerFileKind, case-insensitively. Gallery and unknown
    // types return false with the kind defaulted. Hand-written, not Enum.Parse: Cove's type strings
    // do not map one to one onto the enum names.
    //
    // The plural spellings are accepted because the host singularizes only two of its own: its
    // selection-action normalizer rewrites videos and images and passes every other list's entity
    // type through unchanged, so a bulk action on the texts or audios list arrives here as texts or
    // audios.
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

    // Cove models entity permissions per kind, so renaming an image requires images.write, never the
    // video permission. Throws for a kind this extension does not rename.
    internal static (string Read, string Write) PermissionsFor(RenamerFileKind kind) => kind switch
    {
        RenamerFileKind.Image => (Cove.Core.Auth.Permissions.ImagesRead, Cove.Core.Auth.Permissions.ImagesWrite),
        RenamerFileKind.Audio => (Cove.Core.Auth.Permissions.AudiosRead, Cove.Core.Auth.Permissions.AudiosWrite),
        RenamerFileKind.Text => (Cove.Core.Auth.Permissions.TextsRead, Cove.Core.Auth.Permissions.TextsWrite),
        RenamerFileKind.Video => (Cove.Core.Auth.Permissions.VideosRead, Cove.Core.Auth.Permissions.VideosWrite),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a renamable kind"),
    };

    // Adapts the host's core IJobProgress, handed to the IJobService.Enqueue delegate, to the
    // extension IJobProgress the batch methods consume.
    private sealed class HostProgress(Cove.Core.Interfaces.IJobProgress core) : IJobProgress
    {
        public void Report(double percent, string? message = null) => core.Report(percent, message);
    }

    // The fraction of a chunk's progress bar the planning pass owns, execution taking the rest. The
    // split is cosmetic: both passes scale linearly, so the bar only advances.
    private const double PlanningProgressShare = 0.5;

    // The entities a rename run plans and executes before starting the next chunk. Equal to
    // MaxEntityIdsPerRequest, so one selection is one chunk. A run's plans, projected moves and
    // destination-folder map are released with each chunk, so a whole-library run costs what one full
    // selection costs.
    internal const int RenameChunkEntities = MaxEntityIdsPerRequest;

    // The acting files one operation has offered the journal, and the latch its cap trips. The cap
    // bounds a user action, not a chunk: measured per chunk, a whole-library run would never reach it
    // and the undo it protects would be the partial record the cap exists to refuse. Once tripped it
    // stays tripped for the operation, so a later chunk cannot journal the tail of a run whose head
    // was dropped.
    internal sealed class OperationJournalBudget(string operationId)
    {
        private int _actingFiles;

        public string OperationId { get; } = operationId;

        public bool Suppressed { get; private set; }

        // Returns the operation's running total.
        public int Add(int actingFiles) => _actingFiles += actingFiles;

        public void Suppress() => Suppressed = true;
    }

    // Bounds a single source-path regex match so a catastrophic-backtracking pattern is interrupted
    // and cannot hang the batch. Small because this is a short per-entity string test.
    private static readonly TimeSpan RouteRegexMatchTimeout = TimeSpan.FromMilliseconds(100);

    // Builds the per-batch RouteLookups once. Every user regex is parsed here, with a bounded match
    // timeout, so an invalid pattern is caught and logged at build time and the resolver only ever
    // calls IsMatch.
    private RouteLookups BuildLookups(RenamerOptions o)
    {
        // Exact source-path match mirrors the OS-aware path semantics used elsewhere, so on Windows a
        // rule for "media/incoming" matches a stored "Media/Incoming". Keys are normalized by trimming
        // a trailing slash, and the resolver normalizes the source path the same way before lookup.
        var exact = new Dictionary<string, Destination>(DestinationResolver.SourcePathComparer);
        var regexRules = new List<(Regex Pattern, Destination Dest)>();

        foreach (var rule in o.PathDestinations)
        {
            if (!rule.IsRegex)
            {
                // First wins on a duplicate key, preserving user order.
                exact.TryAdd(DestinationResolver.NormalizeSourcePath(rule.Pattern), rule.Dest);
                continue;
            }

            try
            {
                regexRules.Add((new Regex(rule.Pattern, RegexOptions.None, RouteRegexMatchTimeout), rule.Dest));
            }
            catch (ArgumentException ex)
            {
                // The rule is skipped, not the batch.
                LogInvalidRouteRegex(rule.Pattern, ex.Message);
            }
        }

        // The exclude lookups are built the same way as the routing sets above: the same comparer and
        // normalized keys, the same match timeout, and the same skip-and-log on an invalid pattern.
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
