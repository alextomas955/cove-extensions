using System.Globalization;

namespace Renamer.Engine;

/// <summary>
/// Maps a video pixel height to a resolution label (e.g. 1080 -> "1080p").
/// Fixed mapping (configurable bucketing is out of scope for v1). Pure — no I/O.
/// </summary>
public static class ResolutionLabel
{
    /// <summary>
    /// The lettered/numbered bucket labels <see cref="FromHeight"/> can emit (the raw-height fallback
    /// for &lt;480 is excluded — it is a number, not a fixed label). Shared so the trailing-resolution
    /// de-duplication in <see cref="TemplateEngine"/> stays in lockstep with the buckets defined here.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownLabels = ["4k", "1440p", "1080p", "720p", "480p"];

    /// <summary>
    /// Returns the bucket label for <paramref name="height"/>:
    /// ≥2160→<c>4k</c>, ≥1440→<c>1440p</c>, ≥1080→<c>1080p</c>, ≥720→<c>720p</c>,
    /// ≥480→<c>480p</c>; below 480 returns the raw height with a <c>p</c> suffix (e.g.
    /// <c>368</c>→<c>368p</c>). Boundary values map to their labeled bucket (≥ comparison).
    /// </summary>
    /// <remarks>
    /// The sub-480 fallback is progressive-scan-labelled (<c>{height}p</c>), not a bare number: a real
    /// library labels low-res files <c>[368p]</c> in their own filenames, so emitting a bare <c>368</c>
    /// here would REWRITE an already-correct <c>[368p]</c> down to <c>[368]</c> — a needless rename that
    /// strips the <c>p</c>. Suffixing keeps a correctly-labelled low-res file a no-op instead.
    /// </remarks>
    public static string FromHeight(int height) => height switch
    {
        >= 2160 => "4k",
        >= 1440 => "1440p",
        >= 1080 => "1080p",
        >= 720 => "720p",
        >= 480 => "480p",
        > 0 => height.ToString(CultureInfo.InvariantCulture) + "p",
        _ => string.Empty,
    };
}
