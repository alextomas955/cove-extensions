using Renamer.Options;

using static global::Renamer.Execution.PathOps;

namespace Renamer.Planner;

/// <summary>
/// The path-traversal confinement gate. The engine emits a sanitized folder path, always relative,
/// and decides nothing about whether that destination may be written to; this helper is the boundary
/// before any executor sees a target. PURE: only
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

    /// <summary>Why a confinement check refused a target, or <see cref="None"/> when it did not.</summary>
    /// <remarks>
    /// The two refusals are separated because they ask a user for opposite actions: one is a
    /// permission to widen, the other a name or a destination to shorten. The caller maps each onto
    /// its own <see cref="RenamerStatus"/>.
    /// </remarks>
    public enum ConfinementRejection
    {
        /// <summary>Accepted: the target is inside the permitted area and within the path budget.</summary>
        None,

        /// <summary>The resolved target lies outside the destination root it is measured from.</summary>
        NotAllowed,

        /// <summary>The resolved absolute path exceeds <see cref="RenamerOptions.FullPathMax"/>.</summary>
        TooLong,
    }

    /// <summary>
    /// Result of a confinement check: whether the target was refused, and if so which of the two
    /// refusals it was, so the caller can classify the skip without re-deriving it from the message.
    /// </summary>
    /// <param name="Rejection">The refusal, or <see cref="ConfinementRejection.None"/> on acceptance.</param>
    /// <param name="TargetFolderPath">The resolved absolute target folder (forward-slash), valid only when <see cref="Accepted"/>.</param>
    /// <param name="Reason">Rejection reason when not accepted; null when accepted.</param>
    public readonly record struct ConfinementResult(
        ConfinementRejection Rejection, string TargetFolderPath, string? Reason)
    {
        /// <summary>True iff the resolved absolute target stays inside the permitted area and within MAX_PATH.</summary>
        /// <remarks>
        /// Derived from <see cref="Rejection"/> rather than stored beside it: two fields that must agree
        /// are two fields that can disagree.
        /// </remarks>
        public bool Accepted => Rejection == ConfinementRejection.None;
    }

    /// <summary>
    /// The containment gate. <paramref name="destinationFolder"/> (the engine's relative
    /// folder-template output) is resolved under <paramref name="anchor"/> to a normalized absolute
    /// path - collapsing any <c>..</c> traversal via
    /// <see cref="Path.GetFullPath(string, string)"/> - and then judged:
    /// <list type="bullet">
    /// <item>the resolved target must stay inside <paramref name="anchor"/>, the destination root the
    /// user chose from Cove's own library paths (or the one containing the file). This is what a
    /// destination MEANS;</item>
    /// <item>the <c>..</c> collapse runs BEFORE containment, so a target that walks out of its anchor
    /// (e.g. <c>&lt;root&gt;/../sibling</c>) is rejected;</item>
    /// <item>containment uses an ordinal, separator-normalized prefix check that is NOT fooled by a
    /// sibling like <c>rootEvil</c> vs <c>root</c>;</item>
    /// <item>the resolved ABSOLUTE full path (folder + <paramref name="newBasename"/>) is
    /// length-checked against <see cref="RenamerOptions.FullPathMax"/>, which the engine could not
    /// do because it never sees the root.</item>
    /// </list>
    /// On acceptance, <see cref="ConfinementResult.TargetFolderPath"/> is the resolved absolute
    /// target folder (forward-slash). This is a PURE string decision — no disk access.
    /// </summary>
    /// <param name="anchor">What the destination is measured from: the destination's chosen library root, the library path containing the file, or the file's own folder for an item that does not move.</param>
    /// <param name="destinationFolder">The engine's rendered folder path, always relative.</param>
    /// <param name="newBasename">The rendered basename, measured into the absolute-length re-check.</param>
    /// <param name="options">Supplies <see cref="RenamerOptions.FullPathMax"/>.</param>
    public static ConfinementResult Resolve(
        string anchor,
        string destinationFolder,
        string newBasename,
        RenamerOptions options)
    {
        // Refused BEFORE any combination with the anchor, because a rooted template has no defined
        // answer further down: the combine below trims a leading separator, turning an absolute-looking
        // template into an ordinary subfolder that escapes nothing, while a drive-qualified one reaches
        // GetFullPath with an embedded colon. The engine renders folders relative, so this guards a
        // direct caller of this public member.
        if (Path.IsPathRooted(destinationFolder))
        {
            return new(
                ConfinementRejection.NotAllowed, string.Empty, "folder template is not relative");
        }

        // The anchor, resolved to a normalized absolute path under the fixed base.
        string rootAbs = ToAbsolute(anchor);

        // Target folder: the anchor itself when the engine emitted no folder; else anchor + folder.
        string targetAbs = string.IsNullOrEmpty(destinationFolder)
            ? rootAbs
            : ToAbsolute(Combine(rootAbs, destinationFolder));

        // Containment: the resolved target must be the anchor or a directory UNDER it. Use a
        // boundary-aware ordinal prefix check (rootAbs + separator) so "rootEvil" != "root".
        if (!IsUnderRoot(targetAbs, rootAbs))
        {
            return new(
                ConfinementRejection.NotAllowed, string.Empty,
                "folder template escapes its destination root");
        }

        return WithinBudget(targetAbs, newBasename, options);
    }

    /// <summary>
    /// Accepts <paramref name="targetAbs"/> when the ABSOLUTE full path (folder +
    /// <paramref name="newBasename"/>) fits <see cref="RenamerOptions.FullPathMax"/>, which the engine
    /// could not measure because it never sees the root. The single site of the length comparison.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> because the duplicate-suffix loops re-measure through it once they settle on a
    /// candidate: a loop lengthens the name to free a taken slot, so the name this is first called
    /// with is not the name that gets written. A caller must pass the SAME folder basis the original
    /// call used, or the two verdicts describe different paths.
    /// </remarks>
    internal static ConfinementResult WithinBudget(
        string targetAbs, string newBasename, RenamerOptions options)
    {
        string fullAbs = Combine(targetAbs, newBasename);
        return fullAbs.Length > options.FullPathMax
            ? new(ConfinementRejection.TooLong, string.Empty,
                $"resolved absolute path length {fullAbs.Length} exceeds FullPathMax {options.FullPathMax}")
            : new(ConfinementRejection.None, NormalizeSlash(targetAbs), null);
    }

    /// <summary>
    /// The entry of <paramref name="roots"/> that contains <paramref name="path"/> - the longest one
    /// when several nest - or <c>null</c> when none does.
    /// </summary>
    /// <remarks>
    /// Longest wins so a library declaring both <c>/media</c> and <c>/media/video</c> anchors a file
    /// under the second, on the nearer boundary the user drew around it. Containment and its case
    /// policy are <see cref="IsUnderRoot"/>'s, so the anchor cannot come to disagree with the
    /// allowlist gate about what "inside" means. Blank entries are ignored rather than treated as a
    /// root matching everything.
    /// <para>
    /// The consequence of longest-wins, stated here because this is where the anchor is chosen:
    /// because the anchor is re-resolved on every plan rather than stored, declaring a new Cove
    /// library path INSIDE an existing one moves every item anchored on the outer one beneath it. That
    /// is the one path-lifecycle event which relocates files with no rule changing and nothing in this
    /// extension edited.
    /// </para>
    /// </remarks>
    public static string? ContainingRoot(string path, IReadOnlyList<string> roots)
    {
        string? best = null;
        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            // The answer is the NORMALIZED entry rather than the one supplied, because the one-time
            // options conversion writes this return straight into the stored destination root, which
            // the panel then re-checks against the library-path list it was offered.
            string normalized = NormalizeSlash(root).TrimEnd('/');
            if (IsUnderRoot(path, normalized) && (best is null || normalized.Length > best.Length))
            {
                best = normalized;
            }
        }

        return best;
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
