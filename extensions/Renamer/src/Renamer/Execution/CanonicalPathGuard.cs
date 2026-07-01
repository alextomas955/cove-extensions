using System.Runtime.InteropServices;
using Renamer.Planner;

namespace Renamer.Execution;

/// <summary>
/// The one deliberate read-only-disk touch in the renamer slice: a write-boundary guard that resolves
/// a destination folder's REAL on-disk target and rejects it when that real target escapes every
/// configured allowed root.
///
/// <para>
/// Why this exists at all: the pure string gate (<see cref="PathConfinement"/>) is necessary but
/// provably insufficient on Windows. <see cref="Path.GetFullPath(string)"/> is disk-blind — it
/// collapses <c>..</c> and normalizes separators, but it cannot see a junction or symbolic link
/// physically inside an allowed root that points OUTSIDE it, nor does it expand an 8.3 short name
/// (<c>PROGRA~1</c>). A string-only allowlist is therefore bypassable by a reparse point, an 8.3
/// alias, or a <c>\\?\</c>/device/UNC trick. This guard closes that gap by reading the filesystem.
/// </para>
///
/// <para>
/// It is called by the executor as LATE as the managed API allows — immediately before the disk
/// move — to shrink the window in which a benign destination at preview time could become a
/// junction-to-elsewhere by write time (a time-of-check / time-of-use swap). <see cref="PathConfinement"/>
/// stays 100% pure; this class is the isolated disk-touching layer.
/// </para>
///
/// <para>
/// Fail-closed: every failure path (a vanished or access-denied ancestor, a run off the filesystem
/// root, an oversized 8.3 buffer) classifies as a REJECT. The guard NEVER throws and NEVER accepts on
/// uncertainty — a resolution error is a rejection, not an acceptance.
/// </para>
/// </summary>
public static class CanonicalPathGuard
{
    /// <summary>
    /// The outcome of a <see cref="Check"/>: whether the destination's real on-disk target is
    /// contained by an allowed root, the resolved canonical target (forward-slash) when accepted,
    /// and a human-readable reason when rejected. A non-<see cref="Accepted"/> result is a SKIP, never
    /// a thrown error — mirroring <see cref="DiskMover.MoveResult"/>'s classify-not-throw convention.
    /// </summary>
    /// <param name="Accepted">True iff the resolved real target lies under some allowed root.</param>
    /// <param name="ResolvedTarget">The canonical, link- and 8.3-resolved target (forward-slash); empty when rejected.</param>
    /// <param name="Reason">Why the destination was rejected; null when accepted.</param>
    public readonly record struct GuardResult(bool Accepted, string ResolvedTarget, string? Reason)
    {
        /// <summary>An accepting result carrying the resolved real target.</summary>
        public static GuardResult Accept(string real) => new(true, real, null);

        /// <summary>A rejecting result carrying the reason; the resolved target is empty.</summary>
        public static GuardResult Reject(string reason) => new(false, "", reason);
    }

    /// <summary>
    /// Resolves the REAL on-disk target of <paramref name="targetFolderFwd"/> (following any
    /// junctions/symlinks on its deepest existing ancestor and expanding 8.3 short names) and accepts
    /// it only when that real target is the same as, or under, one of <paramref name="allowedRoots"/>.
    /// Device (<c>\\.\</c>), extended-length (<c>\\?\</c>), and UNC (<c>\\server\share</c>) syntaxes are
    /// rejected unless an allowed root is byte-for-byte that exact prefix form — the <c>\\?\</c> prefix
    /// disables <c>..</c> collapse, so it must never be trusted as an incoming destination here.
    /// Any IO/access error during resolution → REJECT (fail-closed). The guard never throws.
    /// </summary>
    /// <param name="targetFolderFwd">The resolved destination folder (forward-slash) about to be written to.</param>
    /// <param name="allowedRoots">The owner-configured absolute destination roots.</param>
    /// <returns>An accepting <see cref="GuardResult"/> with the canonical target, or a rejecting one with a reason.</returns>
    public static GuardResult Check(string targetFolderFwd, IReadOnlyList<string> allowedRoots)
    {
        string native = targetFolderFwd.Replace('/', Path.DirectorySeparatorChar);

        // (1) Reject device/extended-length/UNC syntaxes unless an allowed root is that EXACT prefix
        //     form. \\?\ in particular tells Windows to SKIP normalization, so a \\?\C:\allowed\..\..\X
        //     would otherwise survive the boundary check with its `..` intact. We never apply the
        //     prefix ourselves (long-path opt-in is a separate concern); we only refuse to be fooled.
        if ((IsExtendedLength(native) || IsDosDevice(native) || IsUnc(native))
            && !allowedRoots.Any(r => SamePrefixForm(r, native)))
        {
            return GuardResult.Reject("destination uses device/UNC/extended-length syntax not in the allowlist");
        }

        // (2-5) Resolve the real on-disk target (deepest-existing-ancestor walk + link resolution +
        //        8.3 expansion). Null = any IO/auth error or a run off the root → fail-closed reject.
        string? real = ResolveRealTargetFolder(native);
        if (real is null)
        {
            return GuardResult.Reject("canonical resolution failed (path vanished / access denied / too many links)");
        }

        // (6) The boundary check on the RESOLVED real path, reusing PathConfinement's single
        //     source-of-truth containment. Each root is canonicalized the same way (long-form,
        //     forward-slash) so both sides of the compare are in their long form.
        bool underSome = allowedRoots.Any(r => PathConfinement.IsUnderRoot(real, CanonicalRoot(r)));

        return underSome
            ? GuardResult.Accept(real)
            : GuardResult.Reject("destination resolves (via link/8.3) outside every allowed root");
    }

    /// <summary>
    /// Walks <paramref name="targetFolderNative"/> up to its deepest ancestor that EXISTS on disk,
    /// resolves any reparse point on that ancestor to its real target, expands 8.3 short names, then
    /// re-appends the non-existent tail. Returns the canonical absolute target (forward-slash), or
    /// <c>null</c> on any IO/access error or a run off the root (fail-closed).
    /// </summary>
    /// <remarks>
    /// The ancestor walk is mandatory: <see cref="Directory.ResolveLinkTarget(string, bool)"/> throws
    /// when the path does not exist (the destination tail does not yet exist), and returns <c>null</c>
    /// when the path is not a link (so a non-link ancestor must fall through to
    /// <see cref="Path.GetFullPath(string)"/>). Calling it on the full non-existent destination would
    /// throw every time.
    /// </remarks>
    private static string? ResolveRealTargetFolder(string targetFolderNative)
    {
        // 1. Find the deepest ancestor that exists, stacking the non-existent leaf segments.
        string? probe = targetFolderNative;
        var tail = new Stack<string>();
        while (probe is not null && !Directory.Exists(probe) && !File.Exists(probe))
        {
            tail.Push(Path.GetFileName(probe));
            probe = Path.GetDirectoryName(probe);
        }

        if (probe is null)
        {
            return null; // ran off the filesystem root → fail-closed
        }

        // 2. Resolve reparse points on the existing ancestor, following the whole chain.
        //    null  => not a link: use its own normalized full path.
        //    non-null => a junction/symlink: use the resolved final target.
        string resolvedAncestor;
        try
        {
            var link = Directory.ResolveLinkTarget(probe, returnFinalTarget: true);
            resolvedAncestor = link?.FullName ?? Path.GetFullPath(probe);
        }
        catch (IOException)
        {
            return null; // vanished between the existence check and the resolve / too many links → fail-closed
        }
        catch (UnauthorizedAccessException)
        {
            return null; // denied → fail-closed
        }

        // 3. Expand 8.3 short names on the resolved ancestor — GetFullPath does NOT do this, so a
        //    PROGRA~1-style alias would otherwise compare unequal to its long form.
        resolvedAncestor = LongPath.Expand(resolvedAncestor);

        // 4. Re-append the non-existent tail to the canonical ancestor.
        string real = resolvedAncestor;
        while (tail.Count > 0)
        {
            real = Path.Combine(real, tail.Pop());
        }

        return real.Replace('\\', '/');
    }

    /// <summary>
    /// Canonicalizes an allowed root the SAME way as a resolved target — including LINK resolution —
    /// so both sides of the containment compare are in real-target form. Without this, an allowlisted
    /// root that is itself (or sits under) a junction/symlink — e.g. <c>C:/data/media</c> where
    /// <c>media</c> is a junction to <c>D:/realmedia</c>, a common pattern for relocating a library to
    /// another volume — would canonicalize to <c>C:/data/media</c> while a legitimate destination under
    /// it resolves (target side) to <c>D:/realmedia/…</c>, spuriously failing containment. Resolving the
    /// root through <see cref="ResolveRealTargetFolder"/> puts both sides in the same real-target space.
    /// Falls back to the normalize+8.3 form when the root cannot be link-resolved (e.g. it does not yet
    /// exist on disk) — that fallback is the original behavior, so a non-link root is unaffected.
    /// </summary>
    private static string CanonicalRoot(string root)
    {
        string native = root.Replace('/', Path.DirectorySeparatorChar);
        return ResolveRealTargetFolder(native)
            ?? LongPath.Expand(Path.GetFullPath(native)).Replace('\\', '/');
    }

    // ── special-prefix predicates: check both separator forms ─────────────
    //
    // These predicates encode WINDOWS path semantics (UNC \\server\share, device \\.\, extended \\?\).
    // The forward-slash variants (//?/, //./, //) are only meaningful on Windows: in Check(), a
    // forward-slash input is first Replace('/', DirectorySeparatorChar)'d, which on Windows turns
    // "//?/X" into "\\?\X" before these run — so the FORWARD-slash forms exist for paths that somehow
    // survive un-converted, and matter only under Windows semantics. The forward-slash forms are gated
    // behind OperatingSystem.IsWindows() so that on a non-Windows host a benign leading "//"
    // (a redundant-separator artifact, where "//x" is just "/x") is NOT misclassified as UNC and
    // spuriously rejected. The backslash forms always apply (a literal backslash is not a path
    // separator off Windows, so it can only be a deliberate special prefix).

    /// <summary>The extended-length prefix <c>\\?\</c>, which disables <c>..</c> collapse.</summary>
    private static bool IsExtendedLength(string p) =>
        p.StartsWith(@"\\?\", StringComparison.Ordinal)
        || (OperatingSystem.IsWindows() && p.StartsWith("//?/", StringComparison.Ordinal));

    /// <summary>The DOS device prefix <c>\\.\</c>.</summary>
    private static bool IsDosDevice(string p) =>
        p.StartsWith(@"\\.\", StringComparison.Ordinal)
        || (OperatingSystem.IsWindows() && p.StartsWith("//./", StringComparison.Ordinal));

    /// <summary>A UNC path (<c>\\server\share</c>) that is not an extended-length or device path.</summary>
    private static bool IsUnc(string p) =>
        (p.StartsWith(@"\\", StringComparison.Ordinal)
         || (OperatingSystem.IsWindows() && p.StartsWith("//", StringComparison.Ordinal)))
        && !IsExtendedLength(p) && !IsDosDevice(p);

    /// <summary>True iff <paramref name="root"/> is byte-for-byte the same special-prefix form as <paramref name="target"/> (so a deliberately-allowlisted UNC/device/extended root is honored).</summary>
    private static bool SamePrefixForm(string root, string target)
    {
        string rootNative = root.Replace('/', Path.DirectorySeparatorChar);
        if (IsExtendedLength(target))
        {
            return IsExtendedLength(rootNative);
        }

        if (IsDosDevice(target))
        {
            return IsDosDevice(rootNative);
        }

        return IsUnc(target) && IsUnc(rootNative);
    }

    /// <summary>
    /// Expands an 8.3 short-name path (<c>PROGRA~1</c>) to its canonical long form via
    /// <c>kernel32!GetLongPathNameW</c>. This is the one platform-specific touch: there is no BCL
    /// equivalent and <see cref="Path.GetFullPath(string)"/> does not expand short names. Returns the
    /// input unchanged off Windows, or when the API cannot expand (missing path / buffer too small).
    /// </summary>
    private static class LongPath
    {
        // A char-buffer (not StringBuilder) marshalling avoids the extra native-to-managed copy the
        // CA1838 analyzer flags; the API writes a null-terminated wide string directly into the span.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathNameW(string lpszShortPath, char[] lpszLongPath, uint cchBuffer);

        /// <summary>Returns the long-form path, or <paramref name="path"/> unchanged when expansion is unavailable.</summary>
        public static string Expand(string path)
        {
            if (!OperatingSystem.IsWindows())
            {
                return path;
            }

            var buffer = new char[short.MaxValue];
            uint len = GetLongPathNameW(path, buffer, (uint)buffer.Length);
            return len > 0 && len < buffer.Length ? new string(buffer, 0, (int)len) : path;
        }
    }
}
