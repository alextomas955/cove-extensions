using Renamer.Options;

namespace Renamer.Planner;

/// <summary>
/// The shared classification vocabulary for a planned per-file renamer. The dry-run planner
/// produces <see cref="Renamer"/>/<see cref="Move"/>/<see cref="NoOp"/>/
/// <see cref="SkipCollision"/>/<see cref="SkipGated"/>; <see cref="SkipLocked"/>,
/// <see cref="SkipBlocked"/> and <see cref="Failed"/> are produced by the executor but defined here
/// so the planner and executor speak one enum. <see cref="SkipMissingSource"/> is produced by BOTH
/// halves — the executor's move-time source pre-check and the preview planner's read-only
/// source-presence check.
/// </summary>
public enum RenamerStatus
{
    /// <summary>In-place basename change (same parent folder).</summary>
    Renamer,

    /// <summary>Basename change AND a parent-folder move.</summary>
    Move,

    /// <summary>The rendered target equals the current path — nothing to do.</summary>
    NoOp,

    /// <summary>
    /// A taken target the suffix loop could not free, OR a folder template that escaped the
    /// library root (confinement rejection). The executor must NOT attempt a move.
    /// </summary>
    SkipCollision,

    /// <summary>Gating (only-organized / require-fields) excluded this item.</summary>
    SkipGated,

    /// <summary>
    /// An exclude rule (tag / studio incl. parent / source-path) matched — the item is
    /// skipped-with-reason for EVERY one of its files (it is never rendered or moved). Kept DISTINCT
    /// from <see cref="SkipGated"/> (a gating skip) so the whole-batch preview and the run log can
    /// attribute an exclude correctly rather than conflating it with a gate. The matched exclude rule
    /// label travels in the item's <see cref="RenamerPlanItem.Reason"/>.
    /// </summary>
    SkipExcluded,

    /// <summary>Executor-only: the source file was locked/in-use at move time.</summary>
    SkipLocked,

    /// <summary>
    /// Executor- AND preview-produced: the source row exists in the DB but its file is absent on
    /// disk. Kept DISTINCT from <see cref="SkipLocked"/> (a file-lock skip) so run output, the log,
    /// and the preview attribute a genuinely-gone file correctly rather than reporting it as in-use.
    /// The executor emits it from a move-time source pre-check; the preview planner emits it from a
    /// read-only source-presence check.
    /// </summary>
    SkipMissingSource,

    /// <summary>
    /// Batch-only: the destination volume dropped below the free-space headroom in flight (a
    /// concurrent writer shrank it between the up-front admit and this item's copy), so the item was
    /// skipped rather than fill the disk. Kept distinct from <see cref="SkipLocked"/> (a file-lock
    /// skip) so log/monitor output attributes a disk-full skip correctly.
    /// </summary>
    SkipNoSpace,

    /// <summary>
    /// Executor-only: the destination was REFUSED by the canonical write-boundary guard
    /// (<c>Renamer.Execution.CanonicalPathGuard</c>) because its real on-disk target resolves
    /// outside every configured allowed root (a junction/symlink/8.3/UNC escape), or its rendered
    /// basename is not a single path segment. A SECURITY denial — kept distinct from
    /// <see cref="SkipCollision"/> (a name-taken skip) so run output and log monitoring can tell a
    /// policy block apart from a benign collision.
    /// </summary>
    SkipBlocked,

    /// <summary>Executor-only: the DB save failed after a disk move and was rolled back.</summary>
    Failed,
}

/// <summary>
/// One file's planned renamer: its current full path, the intended new full path, the
/// classification the executor consumes, and the resolved pieces (new basename + absolute target
/// folder) the executor needs to perform the move. Immutable.
/// </summary>
/// <param name="FileId">The Cove <c>BaseFileEntity.Id</c> this item plans.</param>
/// <param name="OldFullPath">The file's current full path (<c>ParentFolderPath/Basename</c>, forward-slash).</param>
/// <param name="NewFullPath">The intended new full path (forward-slash), or the old path for a <see cref="RenamerStatus.NoOp"/>/skip.</param>
/// <param name="Status">The planner's classification.</param>
/// <param name="NewBasename">The resolved new basename (name + ext) the executor sets on the row.</param>
/// <param name="TargetFolderPath">The resolved absolute target folder path (forward-slash); equals the source folder for an in-place renamer.</param>
/// <param name="Reason">Human-readable reason for a skip/no-op (null for a plain renamer/move).</param>
/// <param name="Suffixed">UI badge signal: true iff the collision suffix loop ran (a number was appended to free the name). Defaults false; set only on the final Renamer/Move item.</param>
/// <param name="Sanitized">UI badge signal: true iff the engine cleaned the rendered name (illegal chars / spaces changed). Defaults false; set only on the final Renamer/Move item.</param>
/// <param name="ResolvedDestinationRoot">The routed destination-root template the <c>DestinationResolver</c> produced; <c>null</c> for a source-confine / legacy in-place item. Present only on a routed Renamer/Move item.</param>
/// <param name="MatchedRule">The resolver's matched-rule label (e.g. <c>"Studio:42(direct)"</c>, <c>"Tag:anime"</c>, <c>"InPlace"</c>) for preview/log. Defaults <c>""</c> on skip/no-op.</param>
/// <param name="TargetVolume">The destination volume (<see cref="Path.GetPathRoot(string)"/> of the resolved absolute target), set only on the final Renamer/Move item; consumed by the free-space sum and the cross-drive preview flag. Defaults <c>""</c>.</param>
public sealed record RenamerPlanItem(
    int FileId,
    string OldFullPath,
    string NewFullPath,
    RenamerStatus Status,
    string NewBasename,
    string TargetFolderPath,
    string? Reason = null,
    bool Suffixed = false,
    bool Sanitized = false,
    string? ResolvedDestinationRoot = null,
    string MatchedRule = "",
    string TargetVolume = "");

/// <summary>
/// The dry-run output of <c>RenamerPlanner.PlanAsync</c>: one <see cref="RenamerPlanItem"/> per
/// physical file of the entity (every file, never just the first), plus the entity id/kind it
/// planned. Carries NO disk/DB mutation — it is a preview only.
/// </summary>
/// <param name="EntityId">The planned entity's id.</param>
/// <param name="Kind">The planned entity's kind.</param>
/// <param name="Items">One item per file, in file order.</param>
public sealed record RenamerPlan(
    int EntityId,
    RenamerFileKind Kind,
    IReadOnlyList<RenamerPlanItem> Items);

/// <summary>
/// The path-traversal confinement gate. The engine emits a relative, sanitized folder path but
/// explicitly does NOT confine <c>..</c> or absolute paths; this helper is the boundary before any
/// executor sees a target. PURE: only
/// <see cref="Path"/> string math (the <c>GetFullPath(path, basePath)</c> overload does not touch
/// disk for these inputs) — no <c>File.</c>/<c>Directory.</c> calls.
/// </summary>
public static class PathConfinement
{
    // A deterministic absolute anchor so a RELATIVE allowed-root (the DTO's forward-slash
    // ParentFolderPath) resolves the same way regardless of the process cwd. The anchor never
    // touches disk; it only gives Path.GetFullPath a fixed base to collapse "."/".." against.
    private static readonly string Anchor =
        OperatingSystem.IsWindows() ? @"C:\__renamer_root__" : "/__renamer_root__";

    /// <summary>
    /// Result of a confinement check. <see cref="Accepted"/> false means the target escaped the
    /// allowed root or exceeded <see cref="RenamerOptions.FullPathMax"/>; the caller classifies a skip.
    /// </summary>
    /// <param name="Accepted">True iff the resolved absolute target stays under the allowed root and within MAX_PATH.</param>
    /// <param name="TargetFolderPath">The resolved absolute target folder (forward-slash), valid only when <see cref="Accepted"/>.</param>
    /// <param name="Reason">Rejection reason when not accepted; null when accepted.</param>
    public readonly record struct ConfinementResult(bool Accepted, string TargetFolderPath, string? Reason);

    /// <summary>
    /// The allowlist gate. <paramref name="destinationFolder"/> (the engine's folder template
    /// output, which may now be ROOTED) is resolved to a normalized absolute path — collapsing
    /// any <c>..</c> traversal via <see cref="Path.GetFullPath(string, string)"/> — and accepted
    /// only when it lands under one of <paramref name="allowedRoots"/>:
    /// <list type="bullet">
    /// <item>when <paramref name="allowedRoots"/> is empty, the original source-confine behavior
    /// applies: the file may only move within <paramref name="legacySourceRoot"/> (its own parent
    /// directory) and a rooted destination is rejected outright;</item>
    /// <item>when roots are configured, a rooted destination is normalized then required to be
    /// under SOME root; a relative destination is first resolved under
    /// <paramref name="legacySourceRoot"/> and then held to the same under-a-root rule;</item>
    /// <item>the <c>..</c> collapse runs BEFORE containment, so a rooted target that walks out of
    /// every root (e.g. <c>&lt;root&gt;/../sibling</c>) is rejected;</item>
    /// <item>containment uses an ordinal, separator-normalized prefix check that is NOT fooled by a
    /// sibling like <c>rootEvil</c> vs <c>root</c>;</item>
    /// <item>the resolved ABSOLUTE full path (folder + <paramref name="newBasename"/>) is
    /// re-checked against <see cref="RenamerOptions.FullPathMax"/> — the engine only measured the
    /// generated portion, so the absolute length including the root must be re-checked here.</item>
    /// </list>
    /// On acceptance, <see cref="ConfinementResult.TargetFolderPath"/> is the resolved absolute
    /// target folder (forward-slash). This is a PURE string decision — no disk access.
    /// </summary>
    public static ConfinementResult Resolve(
        IReadOnlyList<string> allowedRoots,
        string legacySourceRoot,
        string destinationFolder,
        string newBasename,
        RenamerOptions options)
    {
        bool rooted = !string.IsNullOrEmpty(destinationFolder) && Path.IsPathRooted(destinationFolder);

        // No configured roots: the file's own source folder is the sole implicit root, and a rooted
        // destination is refused — the original, narrow confinement.
        if (allowedRoots.Count == 0)
        {
            if (rooted)
            {
                return new(false, string.Empty, "folder template is an absolute/rooted path");
            }

            return ResolveUnderSingleRoot(legacySourceRoot, destinationFolder, newBasename, options);
        }

        // Normalize the target (collapsing "."/"..") BEFORE any containment decision. A rooted
        // destination resolves on its own; a relative one is anchored under the source folder.
        string targetAbs = rooted
            ? ToAbsolute(destinationFolder)
            : ToAbsolute(Combine(ToAbsolute(legacySourceRoot), destinationFolder));

        // Accept only when the normalized target is the same as, or under, one of the allowed roots.
        if (!allowedRoots.Any(r => IsUnderRoot(targetAbs, ToAbsolute(r))))
        {
            return new(false, string.Empty, "destination is not under any allowed root");
        }

        // Re-check the ABSOLUTE full path (folder + new basename) the engine never saw.
        string fullAbs = Combine(targetAbs, newBasename);
        if (fullAbs.Length > options.FullPathMax)
        {
            return new(false, string.Empty,
                $"resolved absolute path length {fullAbs.Length} exceeds FullPathMax {options.FullPathMax}");
        }

        return new(true, NormalizeSlash(targetAbs), null);
    }

    /// <summary>
    /// The original single-root confinement, preserved verbatim for the empty-allowlist fallback
    /// and the back-compat overload. Resolves the RELATIVE <paramref name="relativeFolder"/> (may
    /// be empty = in-place) under <paramref name="allowedRoot"/>, rejecting any <c>..</c> escape or
    /// an over-<see cref="RenamerOptions.FullPathMax"/> absolute target.
    /// </summary>
    public static ConfinementResult Resolve(
        string allowedRoot,
        string relativeFolder,
        string newBasename,
        RenamerOptions options)
    {
        // Absolute/rooted folder templates are rejected outright (they are not a relative move).
        if (!string.IsNullOrEmpty(relativeFolder) && Path.IsPathRooted(relativeFolder))
        {
            return new(false, string.Empty, "folder template is an absolute/rooted path");
        }

        return ResolveUnderSingleRoot(allowedRoot, relativeFolder, newBasename, options);
    }

    /// <summary>
    /// Resolves <paramref name="relativeFolder"/> under <paramref name="allowedRoot"/> and applies
    /// the escape + length checks. The caller is responsible for the rooted-template rejection; this
    /// helper assumes <paramref name="relativeFolder"/> is relative (or empty = in-place).
    /// </summary>
    private static ConfinementResult ResolveUnderSingleRoot(
        string allowedRoot,
        string relativeFolder,
        string newBasename,
        RenamerOptions options)
    {
        // The allowed root, resolved to a normalized absolute path under the fixed anchor.
        string rootAbs = ToAbsolute(allowedRoot);

        // Target folder: in-place when the engine emitted no folder; else root + relativeFolder.
        string targetAbs = string.IsNullOrEmpty(relativeFolder)
            ? rootAbs
            : ToAbsolute(Combine(rootAbs, relativeFolder));

        // Containment: the resolved target must be the root or a directory UNDER it. Use a
        // boundary-aware ordinal prefix check (rootAbs + separator) so "rootEvil" != "root".
        if (!IsUnderRoot(targetAbs, rootAbs))
        {
            return new(false, string.Empty, "folder template escapes the library root");
        }

        // Re-check the ABSOLUTE full path (folder + new basename) the engine never saw.
        string fullAbs = Combine(targetAbs, newBasename);
        if (fullAbs.Length > options.FullPathMax)
        {
            return new(false, string.Empty,
                $"resolved absolute path length {fullAbs.Length} exceeds FullPathMax {options.FullPathMax}");
        }

        return new(true, NormalizeSlash(targetAbs), null);
    }

    /// <summary>Resolves a (possibly relative, forward-slash) path to a normalized absolute form under the anchor, collapsing "."/"..".</summary>
    private static string ToAbsolute(string path)
    {
        // GetFullPath(path, basePath) is pure (no disk) and anchors a relative path deterministically.
        string native = path.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(native, Anchor);
    }

    private static string Combine(string a, string b)
        => a.TrimEnd('/', '\\') + "/" + b.TrimStart('/', '\\');

    private static string NormalizeSlash(string p) => p.Replace('\\', '/');

    /// <summary>
    /// True iff <paramref name="candidate"/> is <paramref name="root"/> itself or lies under it.
    /// Normalizes separators and compares ordinally with a trailing separator on the root so a
    /// sibling ("…/rootEvil") is not mistaken for a child of ("…/root").
    /// </summary>
    /// <remarks>
    /// Exposed <c>internal</c> (not <c>private</c>) so the disk-resolving canonical guard
    /// (<c>Renamer.Execution.CanonicalPathGuard</c>, same assembly) reuses this single source of
    /// truth for boundary-aware containment instead of duplicating the ~8-line check. Tests reach it
    /// via <c>InternalsVisibleTo("Renamer.Tests")</c>.
    /// </remarks>
    internal static bool IsUnderRoot(string candidate, string root)
    {
        string c = NormalizeSlash(candidate).TrimEnd('/');
        string r = NormalizeSlash(root).TrimEnd('/');

        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(c, r, cmp) || c.StartsWith(r + "/", cmp);
    }
}
