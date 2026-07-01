using System.Globalization;

namespace Renamer.Engine;

/// <summary>
/// Maps a video pixel height to a resolution label (e.g. 1080 -> "1080p").
/// Fixed mapping (configurable bucketing is out of scope for v1). Pure — no I/O.
/// </summary>
public static class ResolutionLabel
{
    /// <summary>
    /// Returns the bucket label for <paramref name="height"/>:
    /// ≥2160→<c>4K</c>, ≥1440→<c>1440p</c>, ≥1080→<c>1080p</c>, ≥720→<c>720p</c>,
    /// ≥480→<c>480p</c>; below 480 returns the raw height as a string. Boundary values
    /// map to their labeled bucket (≥ comparison).
    /// </summary>
    public static string FromHeight(int height) => height switch
    {
        >= 2160 => "4K",
        >= 1440 => "1440p",
        >= 1080 => "1080p",
        >= 720 => "720p",
        >= 480 => "480p",
        _ => height.ToString(CultureInfo.InvariantCulture),
    };
}
