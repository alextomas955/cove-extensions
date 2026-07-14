using WhisparrSync.State;

namespace WhisparrSync.Safety;

/// <summary>One detected overlap: a Whisparr root and a Cove root where one contains (or equals) the other.</summary>
/// <remarks>The paths are reported AS SUPPLIED (un-normalized) so the user recognizes them in the warning.</remarks>
internal sealed record RootOverlap(string WhisparrRoot, string CoveRoot);

/// <summary>
/// SEC-02 re-grab-loop guard: detects when a Cove library root overlaps a Whisparr root — either direction
/// of containment, or an exact match. A shared root is where an import-in-place could echo back to Whisparr
/// as a "new" grab, so the extension surfaces a best-effort ADVISORY warning (never a hard gate). Pure and
/// host-free so it is directly unit-testable.
///
/// Comparison mirrors the <c>WhisparrRootGuard</c> discipline: both sides are normalized through the
/// shared <see cref="EventLedger.NormalizePath"/> (separators unified to <c>/</c>, trailing separators
/// trimmed, case-SENSITIVE — the Linux/Docker target, WR-01), then containment is tested at a SEGMENT BOUNDARY so a sibling like
/// <c>/data/media-evil</c> is not treated as inside <c>/data/media</c>. The warning is advisory only —
/// cross-mount / cross-container deployments legitimately see the same library at different paths, so a
/// non-overlap here is not a guarantee and an overlap is not a hard error.
/// </summary>
internal static class RootOverlapDetector
{
    /// <summary>
    /// Returns every (Whisparr root, Cove root) pair where one path contains or equals the other after
    /// normalization. Empty/whitespace roots on either side are ignored; a null collection yields no overlaps.
    /// </summary>
    public static IReadOnlyList<RootOverlap> Detect(
        IReadOnlyCollection<string>? whisparrRoots, IReadOnlyCollection<string>? coveRoots)
    {
        var overlaps = new List<RootOverlap>();
        if (whisparrRoots is null || coveRoots is null)
        {
            return overlaps;
        }

        foreach (var whisparr in whisparrRoots)
        {
            if (string.IsNullOrWhiteSpace(whisparr))
            {
                continue;
            }

            var normalizedWhisparr = EventLedger.NormalizePath(whisparr);
            foreach (var cove in coveRoots)
            {
                if (string.IsNullOrWhiteSpace(cove))
                {
                    continue;
                }

                var normalizedCove = EventLedger.NormalizePath(cove);
                if (Contains(normalizedWhisparr, normalizedCove) || Contains(normalizedCove, normalizedWhisparr))
                {
                    overlaps.Add(new RootOverlap(whisparr, cove));
                }
            }
        }

        return overlaps;
    }

    // Whether <paramref name="inner"/> is at or beneath <paramref name="outer"/>: equal, or a child at a
    // separator boundary (so "/data/media-evil" does NOT count as beneath "/data/media").
    private static bool Contains(string outer, string inner)
        => inner == outer || inner.StartsWith(outer + "/", StringComparison.Ordinal);
}
