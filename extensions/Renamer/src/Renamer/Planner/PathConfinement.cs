using Renamer.Options;

using static global::Renamer.Planner.PathOps;

namespace Renamer.Planner;

// The path-traversal confinement gate. The engine emits a sanitized, always relative folder path and
// decides nothing about whether that destination may be written to; this is the boundary before any
// executor sees a target. Pure string math: the GetFullPath(path, basePath) overload does not touch
// disk for these inputs, and nothing here calls File or Directory.
public static class PathConfinement
{
    // A deterministic absolute anchor so a relative allowed-root (the DTO's forward-slash
    // ParentFolderPath) resolves the same way regardless of the process cwd. The anchor never touches
    // disk; it only gives Path.GetFullPath a fixed base to collapse "." and ".." against.
    private static readonly string Anchor =
        OperatingSystem.IsWindows() ? @"C:\__renamer_root__" : "/__renamer_root__";

    // Why a confinement check refused a target. The two refusals are separated because they ask a user
    // for opposite actions: one is a permission to widen, the other a name or a destination to shorten.
    public enum ConfinementRejection
    {
        None,

        // The resolved target lies outside the destination root it is measured from.
        NotAllowed,

        // The resolved absolute path exceeds RenamerOptions.FullPathMax.
        TooLong,
    }

    // The outcome of a confinement check, carrying which refusal it was so the caller can classify the
    // skip without re-deriving it from the message. TargetFolderPath is the resolved absolute target
    // folder in forward-slash form, valid only on acceptance.
    public readonly record struct ConfinementResult(
        ConfinementRejection Rejection, string TargetFolderPath, string? Reason)
    {
        // Derived from Rejection and not stored beside it: two fields that must agree are two fields
        // that can disagree.
        public bool Accepted => Rejection == ConfinementRejection.None;
    }

    /// <summary>
    /// Resolves the engine's relative folder output under <paramref name="anchor"/> and judges whether
    /// the target may be written to.
    /// </summary>
    /// <remarks>
    /// The ".." collapse runs before containment, so a target that walks out of its anchor is refused.
    /// Containment is an ordinal, separator-normalized prefix check that is not fooled by a sibling
    /// ("rootEvil" against "root"). The resolved absolute full path, folder plus basename, is then
    /// length-checked against <see cref="RenamerOptions.FullPathMax"/>, which the engine cannot do
    /// because it never sees the root. A pure string decision - no disk access.
    /// </remarks>
    public static ConfinementResult Resolve(
        string anchor,
        string destinationFolder,
        string newBasename,
        RenamerOptions options)
    {
        // Refused before any combination with the anchor, because a rooted template has no defined
        // answer further down: the combine below trims a leading separator, turning an absolute-looking
        // template into an ordinary subfolder that escapes nothing, while a drive-qualified one reaches
        // GetFullPath with an embedded colon. The engine renders folders relative, so this guards a
        // direct caller of this public member.
        if (Path.IsPathRooted(destinationFolder))
        {
            return new(
                ConfinementRejection.NotAllowed, string.Empty, "folder template is not relative");
        }

        string rootAbs = ToAbsolute(anchor);

        // The anchor itself when the engine emitted no folder; else anchor plus folder.
        string targetAbs = string.IsNullOrEmpty(destinationFolder)
            ? rootAbs
            : ToAbsolute(Combine(rootAbs, destinationFolder));

        // The resolved target must be the anchor or a directory under it.
        if (!IsUnderRoot(targetAbs, rootAbs))
        {
            return new(
                ConfinementRejection.NotAllowed, string.Empty,
                "folder template escapes its destination root");
        }

        return WithinBudget(targetAbs, newBasename, options);
    }

    // The single site of the absolute-length comparison, which the engine cannot make because it never
    // sees the root. Internal because the duplicate-suffix loops re-measure through it once they settle
    // on a candidate: a loop lengthens the name to free a taken slot, so the name this is first called
    // with is not the name that gets written. A caller must pass the same folder basis the original
    // call used, or the two verdicts describe different paths.
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
    /// The entry of <paramref name="roots"/> containing <paramref name="path"/> - the longest one when
    /// several nest - or <c>null</c> when none does.
    /// </summary>
    /// <remarks>
    /// Longest wins so a library declaring both <c>/media</c> and <c>/media/video</c> anchors a file
    /// under the second, on the nearer boundary the user drew around it. Containment and its case policy
    /// are <see cref="IsUnderRoot"/>'s, so the anchor cannot come to disagree with the allowlist gate
    /// about what "inside" means. Blank entries are ignored. One consequence, stated here because this
    /// is where the anchor is chosen: the anchor is re-resolved on every plan, so declaring a new Cove
    /// library path inside an existing one moves every item anchored on the outer one beneath it.
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

            // The answer is the normalized entry and not the one supplied, because the one-time options
            // conversion writes this return straight into the stored destination root, which the panel
            // then re-checks against the library-path list it was offered.
            string normalized = NormalizeSlash(root).TrimEnd('/');
            if (IsUnderRoot(path, normalized) && (best is null || normalized.Length > best.Length))
            {
                best = normalized;
            }
        }

        return best;
    }

    private static string ToAbsolute(string path)
    {
        // GetFullPath(path, basePath) is pure and anchors a relative path deterministically.
        string native = path.Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(native, Anchor);
    }

    private static string Combine(string a, string b)
        => a.TrimEnd('/', '\\') + "/" + b.TrimStart('/', '\\');


    // True when candidate is root itself or lies under it. Separators are normalized and the compare is
    // ordinal with a trailing separator on the root, so a sibling ".../rootEvil" is not mistaken for a
    // child of ".../root".
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
