using WhisparrSync.Options;
using WhisparrSync.State;

namespace WhisparrSync.Safety;

/// <summary>
/// The Cove→Whisparr path-view rewrite: a Cove file path expressed in the terms the connected Whisparr instance
/// mounts the library under.
/// </summary>
/// <remarks>
/// Composes <see cref="EventLedger.NormalizePath"/> with <see cref="RootOverlapDetector.Contains"/> and admits no
/// third path-comparison rule. It lives beside that containment rule because two sibling capability slices reach
/// it — the add path's root derivation and the acquisition worklist's path leg — and neither may depend on the
/// other. Pure and host-free: it holds no <c>WhisparrClient</c> and no options handle, which is what lets a
/// caller without a transport client ask the question.
/// </remarks>
internal static class PathTranslationService
{
    /// <summary>
    /// Rewrites <paramref name="coveFilePath"/> into the connected Whisparr instance's view of it.
    /// </summary>
    /// <remarks>
    /// The FIRST rule in <paramref name="table"/> whose <c>CovePrefix</c> contains the path at a segment boundary
    /// wins; identity when the table is empty or no rule matches (a shared mount). A later rule that would also
    /// match is never reached, and a rule carrying a blank <c>CovePrefix</c> is skipped — an empty prefix would
    /// otherwise contain every path. Comparison is case-SENSITIVE, inherited from
    /// <see cref="EventLedger.NormalizePath"/>: on the Linux/Docker target <c>/data/Media</c> and
    /// <c>/data/media</c> are different directories, and folding would rewrite a path the admin never mapped
    /// The returned path is always normalized, even on the identity result.
    /// </remarks>
    internal static string ToWhisparrView(string coveFilePath, IReadOnlyList<PathTranslationRule> table)
    {
        var normalized = EventLedger.NormalizePath(coveFilePath);
        foreach (var rule in table)
        {
            var covePrefix = EventLedger.NormalizePath(rule.CovePrefix);
            if (covePrefix.Length > 0 && RootOverlapDetector.Contains(covePrefix, normalized))
            {
                return EventLedger.NormalizePath(rule.WhisparrPrefix) + normalized[covePrefix.Length..];
            }
        }

        return normalized;
    }
}
