using Renamer.Engine;
using Renamer.Options;

using static global::Renamer.Execution.PathOps;

namespace Renamer.Planner;

/// <summary>
/// The dry-run half of the renamer slice: loads a media item via <see cref="IRenamerDataPort"/>
/// (read-only), projects each file with <see cref="MetadataProjector"/>, renders the new name/folder
/// with the pure <see cref="TemplateEngine.Render"/>, applies the path-confinement gate, and
/// classifies each file into a <see cref="RenamerPlanItem"/> — producing a <see cref="RenamerPlan"/>
/// that mutates NOTHING (no <c>File.Move</c>, no <c>SaveChangesAsync</c>, no <c>Directory.Create</c>).
///
/// It owns the plan side of gating, multi-file handling, and collision
/// classification. Execution-time re-checks + the unique-index backstop live in the executor.
/// </summary>
public sealed class RenamerPlanner
{
    private readonly IRenamerDataPort _port;

    /// <summary>Bound on the collision suffix loop before giving up with <see cref="RenamerStatus.SkipCollision"/>.</summary>
    private const int MaxSuffixAttempts = 1000;

    public RenamerPlanner(IRenamerDataPort port) => _port = port;

    /// <summary>
    /// An empty <see cref="RouteLookups"/> (no destination maps, no regex rules). With empty lookups
    /// the resolver always returns <see cref="RouteCategory.Unmatched"/>, so every entity takes the
    /// default destination.
    /// </summary>
    private static readonly RouteLookups EmptyLookups = new(
        new Dictionary<int, Destination>(),
        new Dictionary<int, Destination>(),
        new Dictionary<string, Destination>(),
        Array.Empty<(System.Text.RegularExpressions.Regex, Destination)>());

    /// <summary>
    /// Back-compat overload for callers that do not route (tests, single-entity callers): plans with
    /// <see cref="EmptyLookups"/>, so every file takes the default destination.
    /// </summary>
    public Task<RenamerPlan> PlanAsync(
        RenamerFileKind kind, int entityId, RenamerOptions options, CancellationToken ct)
        => PlanAsync(kind, entityId, options, EmptyLookups, ct);

    /// <summary>Non-routing overload of <see cref="PlanWithEntityAsync(RenamerFileKind,int,RenamerOptions,RouteLookups,CancellationToken)"/>.</summary>
    public Task<PlanResult> PlanWithEntityAsync(
        RenamerFileKind kind, int entityId, RenamerOptions options, CancellationToken ct)
        => PlanWithEntityAsync(kind, entityId, options, EmptyLookups, ct);

    /// <summary>
    /// Computes the per-file old→new plan for the given entity, performing zero disk/DB mutation.
    /// Returns an empty plan when the entity does not exist. Routing is resolved ONCE per entity
    /// (mirroring how <see cref="TryGate"/> runs once): the resolved destination's root becomes the
    /// anchor the per-file confinement length-checks and contains against, so an over-long
    /// destination is a preview skip, never a move-time crash.
    /// </summary>
    /// <param name="kind">The entity kind to plan.</param>
    /// <param name="entityId">The entity id to plan.</param>
    /// <param name="options">The renamer options (template + sanitization + destination maps).</param>
    /// <param name="lookups">The per-batch hoisted routing lookups (built once in <c>RunRenamerBatchAsync</c>); empty = legacy source-confine.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RenamerPlan> PlanAsync(
        RenamerFileKind kind, int entityId, RenamerOptions options, RouteLookups lookups, CancellationToken ct)
        => (await PlanWithEntityAsync(kind, entityId, options, lookups, ct)).Plan;

    /// <summary>
    /// Same as <see cref="PlanAsync(RenamerFileKind,int,RenamerOptions,RouteLookups,CancellationToken)"/>
    /// but also surfaces the entity it loaded.
    /// </summary>
    /// <remarks>
    /// The batch runner needs each file's <c>SizeBytes</c> for the cross-drive free-space sum. Those
    /// sizes live on the loaded entity's files, not on the plan items — so this overload hands back the
    /// same entity the planner already loaded, letting PHASE A read the sizes without a second
    /// (identical, expensive multi-<c>Include</c>) DB load per id. <see cref="PlanResult.Entity"/> is
    /// <c>null</c> exactly when the id is not found (an empty plan + null entity).
    /// </remarks>
    public async Task<PlanResult> PlanWithEntityAsync(
        RenamerFileKind kind, int entityId, RenamerOptions options, RouteLookups lookups, CancellationToken ct)
    {
        var entity = await _port.LoadEntityAsync(kind, entityId, ct);
        if (entity is null)
        {
            return new PlanResult(new RenamerPlan(entityId, kind, Array.Empty<RenamerPlanItem>()), null);
        }

        return new PlanResult(await PlanLoadedEntity(entity, options, lookups, ct), entity);
    }

    /// <summary>
    /// Plans an ALREADY-LOADED entity: route → exclude → gate → per-file classify — the exact plan
    /// logic <see cref="PlanWithEntityAsync(RenamerFileKind,int,RenamerOptions,RouteLookups,CancellationToken)"/>
    /// runs after its own load.
    /// </summary>
    /// <remarks>
    /// Performs NO DB load — the caller supplies the loaded <paramref name="entity"/>. That is the seam
    /// a batch loader uses: load many entities in one round-trip, then plan each here with results
    /// identical to the per-id load-then-plan path.
    /// </remarks>
    public async Task<RenamerPlan> PlanLoadedEntity(
        RenamerEntity entity, RenamerOptions options, RouteLookups lookups, CancellationToken ct)
    {
        // Route ONCE per entity (mirroring how the metadata projector runs once per file), and do it
        // BEFORE gating. Excludes are evaluated first and beat every other reason an item could be
        // skipped — including the gates — so an item that both matches an exclude rule and would be
        // gated is attributed to the exclude, not the gate. This keeps the exclude attribution
        // (SkipExcluded) distinct and accurate in the preview/log even for the overlap case.
        var route = DestinationResolver.Resolve(entity, options, lookups);

        // An excluded entity is a SkipExcluded skip-with-reason for EVERY file (mirrors the gated
        // path) — never rendered or moved, and shown as such in the whole-batch preview rather than
        // silently dropped. SkipExcluded is kept distinct from SkipGated so the preview/log attributes
        // an exclude correctly. The matched exclude rule label travels in the reason.
        if (route.Category == RouteCategory.Excluded)
        {
            var excluded = entity.Files
                .Select(f => SkipItem(f, RenamerStatus.SkipExcluded, $"excluded: {route.MatchedRule}"))
                .ToList();
            return new RenamerPlan(entity.EntityId, entity.Kind, excluded);
        }

        // A gated (non-excluded) item is SkipGated for EVERY file, never rendered.
        if (TryGate(entity, options, out var gateReason))
        {
            var gated = entity.Files
                .Select(f => SkipItem(f, RenamerStatus.SkipGated, gateReason!))
                .ToList();
            return new RenamerPlan(entity.EntityId, entity.Kind, gated);
        }

        // ONE lookup, ONE destination. The route above answered where this item goes, and its answer
        // is a whole destination: a root chosen from Cove's library paths plus a relative template. A
        // matched rule's destination REPLACES the default, and an item no rule matched takes the
        // default.
        var destination = route.Destination
            ?? new Destination { Root = options.FolderRoot, Template = options.FolderTemplate };

        // A chosen root is a REFERENCE into Cove's library paths, so it is re-read here rather than
        // trusted: a root the user has since removed from Cove no longer names anywhere this extension
        // may put a file. Skip the item and say which rule broke, rather than falling through to the
        // default or failing the run.
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

        // The destination's template is substituted into the options rather than threaded past them,
        // because the folder template is what RenamerOptions means by FolderTemplate and every consumer
        // downstream must see the SAME one. A second parameter beside the options would be a second
        // place the effective template could be read from, and the render that forgot it would fall
        // back to the default.
        var effective = options with { FolderTemplate = destination.Template };

        var items = new List<RenamerPlanItem>(entity.Files.Count);
        foreach (var file in entity.Files)         // process every file, never just the first.
        {
            ct.ThrowIfCancellationRequested();
            items.Add(await PlanFileAsync(entity, file, effective, route, destination, ct));
        }

        return new RenamerPlan(entity.EntityId, entity.Kind, items);
    }

    /// <summary>
    /// True iff <paramref name="root"/> is one of Cove's configured library paths - MEMBERSHIP, not
    /// containment, because the value came from a picker offering exactly that list.
    /// </summary>
    /// <remarks>
    /// Compared through <see cref="PathConfinement.IsUnderRoot"/> in both directions rather than by
    /// string equality, so a stored <c>G:/media/</c> still matches a configured <c>G:\media</c>: the
    /// separator style, the trailing slash and the case rule are all that helper's, and this must not
    /// become a second opinion about when two paths name one folder.
    /// </remarks>
    private bool IsLibraryPath(string root)
        => _port.LibraryRoots.Any(configured =>
            !string.IsNullOrWhiteSpace(configured)
            && PathConfinement.IsUnderRoot(root, configured)
            && PathConfinement.IsUnderRoot(configured, root));

    /// <summary>
    /// Gating: only-organized (skip when <c>Organized==false</c>) + require-fields (skip when
    /// a required token projects empty). Returns true with a reason when the item should be gated.
    /// </summary>
    private static bool TryGate(RenamerEntity entity, RenamerOptions options, out string? reason)
    {
        // A configured unorganized destination takes precedence over the only-organized gate: the
        // resolver fires its unorganized route only for an unorganized item, and routing unorganized
        // items to their own destination is the whole point of that route, so an unorganized item with
        // an UnorganizedDestination set is NOT gated here — it falls through to the unorganized route.
        // With no UnorganizedDestination configured, the only-organized gate skips the unorganized item.
        if (options.OnlyOrganized && !entity.Organized && options.UnorganizedDestination is null)
        {
            reason = "skipped: item is not organized (only-organized gate)";
            return true;
        }

        if (options.RequiredFields.Count > 0)
        {
            // A required field is satisfied iff SOME file projects it non-empty. Required fields
            // are entity-level scalars (title/studio/…), so any file's projection suffices.
            var sample = entity.Files.Count > 0 ? entity.Files[0] : null;
            if (sample is not null)
            {
                var (tokens, _, _, _) = MetadataProjector.Project(entity, sample, options);
                foreach (var field in options.RequiredFields)
                {
                    if (!tokens.TryGetValue(field, out var v) || string.IsNullOrEmpty(v))
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

    /// <summary>Classifies a single file: render → anchor → confine → collision → status.</summary>
    private async Task<RenamerPlanItem> PlanFileAsync(
        RenamerEntity entity, RenamerFile file, RenamerOptions options, RouteResult route,
        Destination destination, CancellationToken ct)
    {
        string oldFullPath = JoinPath(file.ParentFolderPath, file.Basename);

        // (1) Project + render (pure). The performer records and tag pairs ride alongside the name
        //     side-input so the engine can order/filter by id before the max limit.
        var (tokens, multi, performers, tagRefs) = MetadataProjector.Project(entity, file, options);
        var rendered = TemplateEngine.Render(tokens, multi, options, performers: performers, tags: tagRefs);
        string newBasename = rendered.Filename + rendered.Ext;

        // (2) Anchor the rendered folder on something the move leaves standing, never on the file's own
        //     parent - see IRenamerDataPort.LibraryRoots. The destination's ROOT is that anchor, and it
        //     has two forms, both library paths Cove owns and no rename can move: one the user picked
        //     from the list, or the one containing this file.
        //
        //     A CHOSEN root always relocates, whatever the template rendered: the user named a folder
        //     out of a list, so an empty render lands the file there. The file's OWN library path names
        //     a library rather than a folder, so with nothing rendered under it the file is already at
        //     its destination and nothing moves. An item that does not move stays measured against its
        //     own parent, so the FullPathMax re-check below sees its real depth.
        bool chosenRoot = destination.Root.Length > 0;
        bool isMove = chosenRoot || rendered.FolderPath.Length > 0;
        string? libraryRoot = chosenRoot
            ? destination.Root
            : isMove ? PathConfinement.ContainingRoot(file.ParentFolderPath, _port.LibraryRoots) : null;

        // Told to measure from the file's own library path, and the file is under none: the destination
        // is not forbidden, it is uncomputable, and every remaining candidate anchor is one the rename
        // itself moves. The item keeps its current name AND folder.
        if (isMove && libraryRoot is null)
        {
            return new RenamerPlanItem(
                file.FileId, oldFullPath, oldFullPath, RenamerStatus.SkipUnanchored,
                file.Basename, file.ParentFolderPath,
                "skipped: this destination measures from the Cove library path holding the file, and "
                    + "this file is under none - add its folder to Cove's library paths, or pick a "
                    + "library path for the destination instead");
        }

        // (2b) Confine: the optional AllowedRoots narrowing, and the single site of the absolute
        //      FullPathMax re-check. Every destination goes through it, so the measured path is real.
        string anchor = isMove ? libraryRoot! : file.ParentFolderPath;
        var confined = PathConfinement.Resolve(
            options.AllowedRoots, anchor, rendered.FolderPath, newBasename, options);
        if (!confined.Accepted)
        {
            return new RenamerPlanItem(
                file.FileId, oldFullPath, oldFullPath,
                confined.Rejection == PathConfinement.ConfinementRejection.TooLong
                    ? RenamerStatus.SkipTooLong
                    : RenamerStatus.SkipNotAllowed,
                file.Basename, file.ParentFolderPath, confined.Reason);
        }

        // Preview should warn "the source is gone" rather than compute a rename target for a file that
        // cannot be moved. The probe runs THROUGH the read-only port seam (never a raw File.Exists in
        // the pure planner) so it stays deterministic/fakeable and never mutates — preview purity is
        // about DB mutation, not disk reads. The item keeps the file at its current path.
        if (!await _port.SourceExistsAsync(oldFullPath, ct))
        {
            return new RenamerPlanItem(
                file.FileId, oldFullPath, oldFullPath, RenamerStatus.SkipMissingSource,
                file.Basename, file.ParentFolderPath, "skipped: source file is missing on disk");
        }

        // The destination is joined from the library anchor rather than read back from the gate: the
        // gate resolves under the synthetic __renamer_root__ when the anchor is not itself absolute and
        // is for MAX_PATH math only. An item that does not move keeps its own parent folder.
        string relTargetFolder = isMove
            ? JoinPath(libraryRoot!, rendered.FolderPath)
            : file.ParentFolderPath;

        // (3) NoOp: the file already sits at its computed destination. Comparing the full target path
        //     (folder + name) — NOT just the basename — is what makes a configured destination that
        //     resolves back to the file's CURRENT folder a no-op. Gating this on `!isMove` (the old
        //     behavior) meant that once any destination/folder-template was set, EVERY file became a
        //     "move", so a file already in its target folder with an unchanged name was reported (and
        //     would be executed) as a move-to-itself. Both parts are forward-slash normalized via
        //     JoinPath, so an ordinal compare is exact.
        string computedFullPath = JoinPath(relTargetFolder, newBasename);
        if (string.Equals(computedFullPath, oldFullPath, StringComparison.Ordinal))
        {
            return new RenamerPlanItem(
                file.FileId, oldFullPath, oldFullPath, RenamerStatus.NoOp,
                file.Basename, relTargetFolder, "no-op: file already at its computed destination");
        }

        // (4) Collision (plan side, NO mutation): resolve the target folder id and apply the
        //     suffix loop until the port reports free, or SkipCollision when exhausted.
        //     For a move, resolve the destination folder id READ-ONLY (never create it during a
        //     dry run — that was the preview-mutation bug). A null id means the destination folder
        //     does not exist yet, so it holds no file rows and no name can collide: the candidate is
        //     free as-is. The executor's PHASE A is the single site that actually creates the folder
        //     when a renamer is performed. An in-place renamer keeps the file's own parent folder id.
        int? targetFolderId = isMove
            ? await _port.TryGetFolderIdAsync(relTargetFolder, ct)
            : file.ParentFolderId;

        string candidate = newBasename;
        int attempt = 0;
        while (targetFolderId is int folderId
            && await _port.CollisionExistsAsync(folderId, candidate, file.FileId, ct))
        {
            attempt++;
            if (attempt > MaxSuffixAttempts)
            {
                return new RenamerPlanItem(
                    file.FileId, oldFullPath, JoinPath(relTargetFolder, newBasename),
                    RenamerStatus.SkipCollision, newBasename, relTargetFolder,
                    $"skipped: no free target name within {MaxSuffixAttempts} suffix attempts");
            }

            candidate = ApplySuffix(rendered.Filename, rendered.Ext, options.DuplicateSuffixFormat, attempt);
        }

        string newFullPath = JoinPath(relTargetFolder, candidate);

        // UI badge signals (set only on the final Renamer/Move item; skip/no-op paths keep the
        // defaults). Suffixed iff the collision loop appended a number; Sanitized via the SAME engine
        // check /preview-sample uses (single source of truth — never string-sniff the basename).
        bool suffixed = attempt > 0;
        bool sanitized = TemplateEngine.WouldSanitizeFilename(tokens, multi, options, performers, tagRefs);

        // Routing facts carried on the final Renamer/Move item (skip/no-op paths keep the defaults).
        // The resolved root is the library path the destination was measured from; null when the item
        // does not move and so anchored on nothing.
        string? resolvedRoot = isMove ? NormalizeSlash(libraryRoot!) : null;

        // TargetVolume feeds the free-space sum and the cross-drive preview flag, so it is derived only
        // where a cross-volume move is possible: a CHOSEN root can be a library path on another drive.
        // An item measured from its own library path stays on the volume it is already on, and
        // reporting one would put same-volume bytes into a cross-drive total. It is derived from the
        // library anchor rather than from confined.TargetFolderPath, which is resolved against the
        // synthetic confinement anchor and would yield a fictitious volume.
        string targetVolume = chosenRoot ? Path.GetPathRoot(ToNative(relTargetFolder)) ?? "" : "";

        return new RenamerPlanItem(
            file.FileId, oldFullPath, newFullPath,
            isMove ? RenamerStatus.Move : RenamerStatus.Renamer,
            candidate, relTargetFolder, null, suffixed, sanitized,
            resolvedRoot, route.MatchedRule, targetVolume);
    }

    /// <summary>Builds a skip/gated item that keeps the file at its current path (no mutation).</summary>
    private static RenamerPlanItem SkipItem(RenamerFile file, RenamerStatus status, string reason)
    {
        string oldFullPath = JoinPath(file.ParentFolderPath, file.Basename);
        return new RenamerPlanItem(file.FileId, oldFullPath, oldFullPath, status, file.Basename, file.ParentFolderPath, reason);
    }
}

/// <summary>The dry-run plan plus the entity it was computed from (<c>null</c> when the id was not found).</summary>
public readonly record struct PlanResult(RenamerPlan Plan, RenamerEntity? Entity);
