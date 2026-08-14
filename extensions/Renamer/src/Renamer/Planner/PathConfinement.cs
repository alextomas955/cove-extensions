using Renamer.Options;

using static global::Renamer.Execution.PathOps;

namespace Renamer.Planner;

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

        // Case rule and its caveats: see PathOps.PathsEqual. Where the volume folds case, a
        // differently-cased path IS physically inside the root, so refusing it was the bug.
        var cmp = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return string.Equals(c, r, cmp) || c.StartsWith(r + "/", cmp);
    }
}
