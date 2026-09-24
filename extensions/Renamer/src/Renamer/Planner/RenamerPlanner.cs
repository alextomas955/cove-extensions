using Renamer.Engine;
using Renamer.Options;

using static global::Renamer.Planner.PathOps;

namespace Renamer.Planner;

// The dry-run half of the renamer slice. It loads a media item read-only, projects each file,
// renders the new name and folder, applies the path-confinement gate and classifies each file.
// Planning mutates nothing: no file move, no save, no directory creation. Execution-time re-checks
// and the unique-index backstop live in the executor.
public sealed class RenamerPlanner
{
    private readonly IRenamerDataPort _port;

    // Bound on the collision suffix loop before giving up with SkipCollision.
    private const int MaxSuffixAttempts = 1000;

    public RenamerPlanner(IRenamerDataPort port) => _port = port;

    // With empty lookups the resolver always returns Unmatched, so every entity takes the default
    // destination.
    // Computes the per-file old-to-new plan for the given entity with zero disk or DB mutation, and
    // returns an empty plan when the entity does not exist. Routing is resolved once per entity, and
    // the resolved destination's root is the anchor the per-file confinement measures against, so an
    // over-long destination is a preview skip and never a move-time crash.
    public async Task<RenamerPlan> PlanAsync(
        RenamerFileKind kind, int entityId, RenamerOptions options, RouteLookups lookups, CancellationToken ct)
    {
        var entity = await _port.LoadEntityAsync(kind, entityId, ct);
        return entity is null
            ? new RenamerPlan(entityId, kind, Array.Empty<RenamerPlanItem>())
            : await PlanLoadedEntity(entity, options, lookups, ct);
    }

    // Plans an already-loaded entity, performing no DB load of its own. That is the seam a batch
    // loader uses: load many entities in one round-trip, then plan each one here.
    public async Task<RenamerPlan> PlanLoadedEntity(
        RenamerEntity entity, RenamerOptions options, RouteLookups lookups, CancellationToken ct)
    {
        // Routing runs once per entity, and before gating. Excludes are evaluated first and beat every
        // other reason an item could be skipped, gates included, so an item that both matches an
        // exclude rule and would be gated is attributed to the exclude.
        var route = DestinationResolver.Resolve(entity, options, lookups);

        // An excluded entity is a skip-with-reason for every one of its files, never rendered or
        // moved. The status is kept apart from SkipGated so the preview and the log attribute an
        // exclude correctly. The matched exclude rule label travels in the reason.
        if (route.Category == RouteCategory.Excluded)
        {
            var excluded = entity.Files
                .Select(f => SkipItem(f, RenamerStatus.SkipExcluded, $"excluded: {route.MatchedRule}"))
                .ToList();
            return new RenamerPlan(entity.EntityId, entity.Kind, excluded);
        }

        if (route.Category == RouteCategory.RuleTimedOut)
        {
            var undecided = entity.Files
                .Select(f => SkipItem(
                    f, RenamerStatus.SkipRuleTimedOut,
                    $"skipped: the rule {route.MatchedRule} timed out matching this item's folder, so "
                        + "whether it applies is unknown - simplify the pattern"))
                .ToList();
            return new RenamerPlan(entity.EntityId, entity.Kind, undecided);
        }

        // A gated item is skipped for every one of its files and never rendered.
        if (TryGate(entity, options, out var gateReason))
        {
            var gated = entity.Files
                .Select(f => SkipItem(f, RenamerStatus.SkipGated, gateReason!))
                .ToList();
            return new RenamerPlan(entity.EntityId, entity.Kind, gated);
        }

        // The title is an entity-level scalar, so it is derived once and every file of the item plans
        // against the one value the executor will record. See MetadataProjector.DerivedTitle.
        string? derivedTitle = MetadataProjector.DerivedTitle(entity, options);

        // One lookup, one destination. A matched rule's destination replaces the default; an item no
        // rule matched takes the kind's own default when it has one, otherwise the global pair.
        var destination = route.Destination
            ?? options.KindDestination(entity.Kind)
            ?? new Destination { Root = options.FolderRoot, Template = options.FolderTemplate };

        // A chosen root is a reference into Cove's library paths, so it is re-read here: a root the
        // user has since removed from Cove no longer names anywhere this extension may put a file.
        // The item is skipped with the broken rule named, and the run does not fail.
        if (destination.Root.Length > 0 && !IsLibraryPath(destination.Root))
        {
            var orphaned = entity.Files
                .Select(f => SkipItem(
                    f, RenamerStatus.SkipRootMissing,
                    $"skipped: the destination root '{destination.Root}' chosen for rule "
                        + $"'{route.MatchedRule}' is no longer one of Cove's library paths - "
                        + "re-pick it, or add that folder back to Cove's library paths"))
                .ToList();
            return new RenamerPlan(entity.EntityId, entity.Kind, orphaned);
        }

        // The destination's template is substituted into the options because the folder template is
        // what RenamerOptions means by FolderTemplate, and every consumer downstream must see the one
        // effective value.
        var effective = options with { FolderTemplate = destination.Template };

        // The destination paths this plan has already handed out. The port's collision check sees only
        // existing rows, and a sibling of the plan being built is not one yet, so without this two
        // files of one entity that render one name are planned at one path. Bounded by the entity's
        // own file count.
        //
        // Membership follows the platform's own case rule, because what it decides is whether two
        // planned paths name one file on disk.
        var claimedTargets = new HashSet<string>(PathComparer);
        var context = new EntityPlanContext(entity, effective, route, destination, derivedTitle);

        var items = new List<RenamerPlanItem>(entity.Files.Count);
        foreach (var file in entity.Files)
        {
            ct.ThrowIfCancellationRequested();
            var item = await PlanFileAsync(context, file, claimedTargets, ct);
            items.Add(item);

            // This loop is the set's only writer and the callee only reads it. Claimed only for an item
            // that places the file: a no-op or a skip leaves the file where its own row already records
            // it, so the port's row check sees it.
            if (item.Status is RenamerStatus.Rename or RenamerStatus.Move)
            {
                claimedTargets.Add(item.NewFullPath);
            }
        }

        return new RenamerPlan(entity.EntityId, entity.Kind, items);
    }

    // Membership in Cove's configured library paths, not containment: the value came from a picker
    // offering exactly that list. Compared through PathConfinement.IsUnderRoot in both directions so a
    // stored "G:/media/" still matches a configured "G:\media", and so this does not become a second
    // opinion about when two paths name one folder.
    private bool IsLibraryPath(string root)
        => _port.LibraryRoots.Any(configured =>
            !string.IsNullOrWhiteSpace(configured)
            && PathConfinement.IsUnderRoot(root, configured)
            && PathConfinement.IsUnderRoot(configured, root));

    // Gating: the kind switch, only-organized, and require-fields. Returns true with a reason when the
    // item should be gated.
    private static bool TryGate(RenamerEntity entity, RenamerOptions options, out string? reason)
    {
        // The kind switch answers a different question from the other gates: they decide whether this
        // item qualifies, while this one says the extension does not rename the kind at all.
        // Whole-library paths drop a disabled kind before walking it, so this gate is what a
        // selection-based rename of a disabled kind meets.
        if (!options.IsKindEnabled(entity.Kind))
        {
            reason = $"skipped: renaming is turned off for {entity.Kind.ToString().ToLowerInvariant()} items";
            return true;
        }

        // A configured unorganized destination takes precedence over the only-organized gate: routing
        // unorganized items to their own destination is the point of that route, so an unorganized item
        // with an UnorganizedDestination set falls through to it. With none configured, the
        // only-organized gate skips the item.
        if (options.OnlyOrganized && !entity.Organized && options.UnorganizedDestination is null)
        {
            reason = "skipped: item is not organized (only-organized gate)";
            return true;
        }

        if (options.RequiredFields.Count > 0)
        {
            // Required fields are entity-level, so the first file's projection answers for every file.
            var sample = entity.Files.Count > 0 ? entity.Files[0] : null;
            if (sample is not null)
            {
                var (tokens, multi) = MetadataProjector.Project(entity, sample, options);
                foreach (var field in options.RequiredFields)
                {
                    if (TemplateEngine.ResolveField(tokens, multi, options, field, entity.Performers, entity.TagRefs).Length == 0)
                    {
                        reason = $"skipped: required field '{field}' is empty (require-fields gate)";
                        return true;
                    }
                }
            }
        }

        reason = null;
        return false;
    }

    // Classifies a single file: render, anchor, confine, collision, status. claimedTargets is the
    // caller's set of destination paths already handed out in this plan, read-only here.
    private async Task<RenamerPlanItem> PlanFileAsync(
        EntityPlanContext context, RenamerFile file, HashSet<string> claimedTargets, CancellationToken ct)
    {
        var (entity, options, route, destination, derivedTitle) = context;
        string oldFullPath = JoinPath(file.ParentFolderPath, file.Basename);

        // Project and render, both pure. The performer records and tag pairs ride alongside the name
        // side-input so the engine can order and filter by id before the max limit.
        var (tokens, multi) = MetadataProjector.Project(entity, file, options);
        var rendered = TemplateEngine.Render(tokens, multi, options, entity.Performers, entity.TagRefs);
        string newBasename = rendered.Filename + rendered.Ext;

        // The rendered folder is anchored on something the move leaves standing, never on the file's
        // own parent - see IRenamerDataPort.LibraryRoots. The destination's root is that anchor, in one
        // of two forms, both library paths Cove owns and no rename can move: one the user picked from
        // the list, or the one containing this file.
        //
        // A chosen root always relocates, whatever the template rendered, because the user named a
        // folder out of a list. The file's own library path names a library rather than a folder, so
        // with nothing rendered under it the file is already at its destination and nothing moves. An
        // item that does not move stays measured against its own parent, so the FullPathMax re-check
        // below sees its real depth.
        bool chosenRoot = destination.Root.Length > 0;
        bool isMove = chosenRoot || rendered.FolderPath.Length > 0;
        string? libraryRoot = LibraryRootFor(file, destination, isMove);

        // Told to measure from the file's own library path, and the file is under none: the destination
        // is not forbidden, it is uncomputable, and every remaining candidate anchor is one the rename
        // itself moves. The item keeps its current name and folder.
        if (isMove && libraryRoot is null)
        {
            return new RenamerPlanItem(
                file.FileId, oldFullPath, oldFullPath, RenamerStatus.SkipUnanchored,
                file.Basename, file.ParentFolderPath,
                "skipped: this destination measures from the Cove library path holding the file, and "
                    + "this file is under none - add its folder to Cove's library paths, or pick a "
                    + "library path for the destination instead");
        }

        // Confinement: containment in the destination's own root, and the single site of the absolute
        // FullPathMax re-check. Every destination goes through it, so the measured path is real.
        string anchor = isMove ? libraryRoot! : file.ParentFolderPath;
        var confined = PathConfinement.Resolve(
            anchor, rendered.FolderPath, newBasename, options);
        if (!confined.Accepted)
        {
            return new RenamerPlanItem(
                file.FileId, oldFullPath, oldFullPath, RejectionStatus(confined.Rejection),
                file.Basename, file.ParentFolderPath, confined.Reason);
        }

        // Preview warns that the source is gone rather than computing a target for a file that cannot
        // be moved. The probe runs through the read-only port seam so it stays deterministic and
        // fakeable; preview purity is about DB mutation, not disk reads.
        if (!await _port.SourceExistsAsync(oldFullPath, ct))
        {
            return new RenamerPlanItem(
                file.FileId, oldFullPath, oldFullPath, RenamerStatus.SkipMissingSource,
                file.Basename, file.ParentFolderPath, "skipped: source file is missing on disk");
        }

        // The destination is joined from the library anchor and not read back from the gate: the gate
        // resolves under the synthetic __renamer_root__ when the anchor is not itself absolute, and is
        // for MAX_PATH math only. An item that does not move keeps its own parent folder.
        string relTargetFolder = isMove
            ? JoinPath(libraryRoot!, rendered.FolderPath)
            : file.ParentFolderPath;

        // No-op: the file already sits at its computed destination. The whole target path is compared,
        // folder and name, so a configured destination that resolves back to the file's current folder
        // is a no-op and not a move to itself. Both parts are forward-slash normalized through
        // JoinPath, so an ordinal compare is exact.
        string computedFullPath = JoinPath(relTargetFolder, newBasename);
        if (string.Equals(computedFullPath, oldFullPath, StringComparison.Ordinal))
        {
            return new RenamerPlanItem(
                file.FileId, oldFullPath, oldFullPath, RenamerStatus.NoOp,
                file.Basename, relTargetFolder, "no-op: file already at its computed destination");
        }

        // Collision, plan side and without mutation: resolve the target folder id and apply the suffix
        // loop until the port reports free, or SkipCollision when exhausted. For a move the destination
        // folder id is resolved read-only, never created during a dry run. A null id means the folder
        // does not exist yet, so it holds no file rows and no name can collide with an existing file; a
        // sibling of this same plan is not a row, which is why the claim check below is not gated on
        // this id. An in-place rename keeps the file's own parent folder id.
        int? targetFolderId = isMove
            ? await _port.TryGetFolderIdAsync(relTargetFolder, ct)
            : file.ParentFolderId;

        var settled = await FindFreeNameAsync(
            rendered, relTargetFolder, targetFolderId, file.FileId, options.DuplicateSuffixFormat,
            claimedTargets, ct);
        if (settled is not { } free)
        {
            return new RenamerPlanItem(
                file.FileId, oldFullPath, JoinPath(relTargetFolder, newBasename),
                RenamerStatus.SkipCollision, newBasename, relTargetFolder,
                $"skipped: no free target name within {MaxSuffixAttempts} suffix attempts");
        }

        var (candidate, attempt) = free;

        // Re-measure the budget against the settled candidate. The confinement gate ran before the loop
        // above, which lengthens the name by whatever DuplicateSuffixFormat spells and repeats up to
        // MaxSuffixAttempts, so an item accepted at the budget can leave the loop past it. Measured
        // against confined.TargetFolderPath, the basis the first measurement used: the gate resolves
        // under the synthetic anchor when the anchor is not itself absolute, and a different basis would
        // describe a different path.
        var recheck = PathConfinement.WithinBudget(confined.TargetFolderPath, candidate, options);
        if (!recheck.Accepted)
        {
            return new RenamerPlanItem(
                file.FileId, oldFullPath, oldFullPath, RenamerStatus.SkipTooLong,
                file.Basename, file.ParentFolderPath, recheck.Reason);
        }

        string newFullPath = JoinPath(relTargetFolder, candidate);

        // The suffix loop can settle on the name this file already carries, so the no-op verdict reached
        // before the loop cannot stand for one after it. Classified as an act, such an item is executed
        // and saved, and on the auto-rename path a save is what makes the host re-raise the update event
        // that re-plans it.
        //
        // Ordinal, not PathsEqual: both parts went through JoinPath so separators are exact, and a
        // target differing from the source by case alone is a rename the user asked for.
        if (string.Equals(newFullPath, oldFullPath, StringComparison.Ordinal))
        {
            return new RenamerPlanItem(
                file.FileId, oldFullPath, oldFullPath, RenamerStatus.NoOp,
                file.Basename, relTargetFolder,
                "no-op: the name this file would take is in use by another file in this folder, and the "
                    + "next free numbered name is the one this file already has");
        }

        // UI badge signals, set only on a final acting item; skip and no-op paths keep the defaults.
        // Sanitized reads the engine's own check, the same one the preview sample uses, so the basename
        // is never string-sniffed.
        bool suffixed = attempt > 0;
        bool sanitized = TemplateEngine.WouldSanitizeFilename(tokens, multi, options, entity.Performers, entity.TagRefs);

        // The resolved root is the library path the destination was measured from; null when the item
        // does not move and so is anchored on nothing.
        string? resolvedRoot = isMove ? NormalizeSlash(libraryRoot!) : null;

        // TargetVolume feeds the free-space sum and the cross-drive preview flag, so it is derived only
        // where a cross-volume move is possible: a chosen root can be a library path on another drive.
        // An item measured from its own library path stays on the volume it is already on, and reporting
        // one would put same-volume bytes into a cross-drive total. It comes from the library anchor;
        // confined.TargetFolderPath is resolved against the synthetic confinement anchor and would yield
        // a fictitious volume.
        string targetVolume = chosenRoot ? Path.GetPathRoot(ToNative(relTargetFolder)) ?? "" : "";

        return new RenamerPlanItem(
            file.FileId, oldFullPath, newFullPath,
            isMove ? RenamerStatus.Move : RenamerStatus.Rename,
            candidate, relTargetFolder, null, suffixed, sanitized,
            resolvedRoot, route.MatchedRule, targetVolume, derivedTitle);
    }

    // The anchor a moving file is measured from. Null when the file does not move, and null when it
    // moves from its own library path and no library path holds it.
    private string? LibraryRootFor(RenamerFile file, Destination destination, bool isMove)
    {
        if (destination.Root.Length > 0)
        {
            return destination.Root;
        }

        return isMove ? PathConfinement.ContainingRoot(file.ParentFolderPath, _port.LibraryRoots) : null;
    }

    private static RenamerStatus RejectionStatus(PathConfinement.ConfinementRejection rejection)
        => rejection == PathConfinement.ConfinementRejection.TooLong
            ? RenamerStatus.SkipTooLong
            : RenamerStatus.SkipNotAllowed;

    // The first free name in the target folder and the suffix attempt that produced it, or null when
    // every attempt up to MaxSuffixAttempts is taken.
    //
    // A candidate is taken by a path this plan has already handed out exactly as it is by an
    // existing row. The in-memory test runs first so a claimed candidate costs no round trip, and it
    // compares the whole candidate path: a non-moving file keeps its own folder, and a whole-path
    // comparison cannot be wrong about which folder a claim was made in.
    private async Task<(string Candidate, int Attempt)?> FindFreeNameAsync(
        RenamerResult rendered, string relTargetFolder, int? targetFolderId, int fileId,
        string suffixFormat, HashSet<string> claimedTargets, CancellationToken ct)
    {
        string candidate = rendered.Filename + rendered.Ext;
        int attempt = 0;
        while (claimedTargets.Contains(JoinPath(relTargetFolder, candidate))
            || (targetFolderId is int folderId
            && await _port.CollisionExistsAsync(folderId, candidate, fileId, ct)))
        {
            attempt++;
            if (attempt > MaxSuffixAttempts)
            {
                return null;
            }

            candidate = ApplySuffix(rendered.Filename, rendered.Ext, suffixFormat, attempt);
        }

        return (candidate, attempt);
    }

    // The entity-level values every file of one entity plans against. Options carries the resolved
    // destination's folder template.
    private readonly record struct EntityPlanContext(
        RenamerEntity Entity, RenamerOptions Options, RouteResult Route, Destination Destination,
        string? DerivedTitle);

    // A skip item keeps the file at its current path.
    private static RenamerPlanItem SkipItem(RenamerFile file, RenamerStatus status, string reason)
    {
        string oldFullPath = JoinPath(file.ParentFolderPath, file.Basename);
        return new RenamerPlanItem(file.FileId, oldFullPath, oldFullPath, status, file.Basename, file.ParentFolderPath, reason);
    }
}
