using System.Globalization;
using Renamer.Engine;
using Renamer.Options;

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
    /// An empty <see cref="RouteLookups"/> (no destination maps, no regex rules) — the legacy,
    /// non-routing behavior every entity gets through the parameterless overload. With empty lookups
    /// the resolver always returns <see cref="RouteCategory.SourceConfine"/>, so the anchor stays the
    /// file's own parent folder exactly as before this phase.
    /// </summary>
    private static readonly RouteLookups EmptyLookups = new(
        new Dictionary<int, string>(),
        new Dictionary<string, string>(),
        new Dictionary<string, string>(),
        Array.Empty<(System.Text.RegularExpressions.Regex, string)>());

    /// <summary>
    /// Back-compat overload for callers that do not route (tests, single-entity callers): plans with
    /// <see cref="EmptyLookups"/>, which yields legacy source-confine behavior for every file.
    /// </summary>
    public Task<RenamerPlan> PlanAsync(
        RenamerFileKind kind, int entityId, RenamerOptions options, CancellationToken ct)
        => PlanAsync(kind, entityId, options, EmptyLookups, ct);

    /// <summary>
    /// Computes the per-file old→new plan for the given entity, performing zero disk/DB mutation.
    /// Returns an empty plan when the entity does not exist. Routing is resolved ONCE per entity
    /// (mirroring how <see cref="TryGate"/> runs once): the resolved destination root becomes the
    /// anchor the per-file confinement length-checks and contains against, so an over-long routed
    /// destination is a preview skip, never a move-time crash.
    /// </summary>
    /// <param name="kind">The entity kind to plan.</param>
    /// <param name="entityId">The entity id to plan.</param>
    /// <param name="options">The renamer options (template + sanitization + destination maps).</param>
    /// <param name="lookups">The per-batch hoisted routing lookups (built once in <c>RunRenamerBatchAsync</c>); empty = legacy source-confine.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<RenamerPlan> PlanAsync(
        RenamerFileKind kind, int entityId, RenamerOptions options, RouteLookups lookups, CancellationToken ct)
    {
        var entity = await _port.LoadEntityAsync(kind, entityId, ct);
        if (entity is null)
        {
            return new RenamerPlan(entityId, kind, Array.Empty<RenamerPlanItem>());
        }

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

        var items = new List<RenamerPlanItem>(entity.Files.Count);
        foreach (var file in entity.Files)         // process every file, never just the first.
        {
            ct.ThrowIfCancellationRequested();
            items.Add(await PlanFileAsync(entity, file, options, route, ct));
        }

        return new RenamerPlan(entity.EntityId, entity.Kind, items);
    }

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
        if (options.OnlyOrganized && !entity.Organized
            && string.IsNullOrEmpty(options.UnorganizedDestination))
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
                var (tokens, _, _) = MetadataProjector.Project(entity, sample, options);
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

    /// <summary>Classifies a single file: render → confine → collision → status.</summary>
    private async Task<RenamerPlanItem> PlanFileAsync(
        RenamerEntity entity, RenamerFile file, RenamerOptions options, RouteResult route, CancellationToken ct)
    {
        string oldFullPath = JoinPath(file.ParentFolderPath, file.Basename);

        // (1) Project + render (pure). The performer records ride alongside the name side-input so
        //     the engine can order/filter performers by id/favorite/gender before the max limit.
        var (tokens, multi, performers) = MetadataProjector.Project(entity, file, options);
        var rendered = TemplateEngine.Render(tokens, multi, options, performers: performers);
        string newBasename = rendered.Filename + rendered.Ext;

        // (2) Confine: the configured AllowedRoots are the permitted destinations. When a route
        //     matched, the ROUTED destination root anchors the relative FolderPath (and the existing
        //     FullPathMax re-check therefore measures the real routed absolute path — no new length
        //     code). SourceConfine keeps the legacy file-own-parent anchor. The routed root is just a
        //     new anchor fed into the SAME gate — no IsPathRooted bypass is added.
        string anchor = route.Category == RouteCategory.SourceConfine
            ? file.ParentFolderPath
            : route.DestinationRootTemplate!;
        var confined = PathConfinement.Resolve(
            options.AllowedRoots, anchor, rendered.FolderPath, newBasename, options);
        if (!confined.Accepted)
        {
            return new RenamerPlanItem(
                file.FileId, oldFullPath, oldFullPath, RenamerStatus.SkipCollision,
                file.Basename, file.ParentFolderPath, confined.Reason);
        }

        // A move happens whenever the file leaves its own source folder. Two independent causes:
        // the folder template rendered a subfolder, OR a routing rule matched and pointed the file at
        // a different destination root. A matched route with an EMPTY folder template still relocates
        // the file to the root of its routed destination — so routing alone makes it a move, even
        // when no subfolder was rendered. Gating only on the rendered subfolder would silently renamer
        // a routed file in place under its source folder while the preview reported it as routed.
        bool routed = route.Category != RouteCategory.SourceConfine;
        bool isMove = routed || !string.IsNullOrEmpty(rendered.FolderPath);

        // The target folder the executor moves to MUST follow the route. For a ROUTED move the
        // confinement gate already resolved the routed destination root + the rendered subfolder into
        // an absolute target (confined.TargetFolderPath, e.g. "D:/studios/acme/Films") — use it, so the
        // executed move lands on the routed destination instead of silently staying under the source
        // folder. For a SOURCE-CONFINE item we keep the legacy file-own-parent relative form (the
        // confined path there is anchored under the synthetic __renamer_root__ and is for MAX_PATH math
        // only, not the real move target), and an in-place item keeps its own parent folder.
        string relTargetFolder = isMove
            ? (route.Category == RouteCategory.SourceConfine
                ? JoinPath(file.ParentFolderPath, rendered.FolderPath)
                : confined.TargetFolderPath)
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
        bool sanitized = TemplateEngine.WouldSanitizeFilename(tokens, multi, options);

        // Routing facts carried on the final Renamer/Move item (skip/no-op paths keep the defaults).
        // ResolvedDestinationRoot is null for a source-confine (legacy in-place) item; TargetVolume is
        // the destination volume's root (VolumeClassifier semantics) for the free-space sum + preview.
        string? resolvedRoot = route.Category == RouteCategory.SourceConfine ? null : route.DestinationRootTemplate;
        // A source-confine item moves in place (no cross-volume transfer), so it has no
        // destination volume of interest. Do NOT derive TargetVolume from confined.TargetFolderPath
        // for it — with an empty AllowedRoots that path is resolved against the SYNTHETIC confinement
        // anchor (C:\__renamer_root__), so Path.GetPathRoot would yield a fictitious "C:/" that does not
        // correspond to the file's real disk and would mis-attribute bytes if a future free-space
        // pre-check trusted it. Leave it empty for source-confine; derive it from the real resolved
        // (routed) target only for an actual routed move.
        string targetVolume = route.Category == RouteCategory.SourceConfine
            ? ""
            : Path.GetPathRoot(confined.TargetFolderPath) ?? "";

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

    /// <summary>Inserts the suffix counter before the extension (e.g. "name" + " ({n})" + ".mkv" → "name (1).mkv").</summary>
    private static string ApplySuffix(string filename, string ext, string suffixFormat, int counter)
    {
        string suffix = suffixFormat.Replace("{n}", counter.ToString(CultureInfo.InvariantCulture));
        return filename + suffix + ext;
    }

    /// <summary>Joins two forward-slash path parts, trimming a single boundary separator; skips an empty part.</summary>
    private static string JoinPath(string a, string b)
    {
        if (string.IsNullOrEmpty(a))
        {
            return b;
        }

        if (string.IsNullOrEmpty(b))
        {
            return a;
        }

        return a.TrimEnd('/') + "/" + b.TrimStart('/');
    }
}
