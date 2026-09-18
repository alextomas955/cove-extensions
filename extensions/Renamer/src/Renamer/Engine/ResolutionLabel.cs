using System.Globalization;

namespace Renamer.Engine;

// Maps a video pixel height to a resolution label. The bucketing is fixed, not configurable.
public static class ResolutionLabel
{
    // The fixed bucket labels FromHeight can emit. The sub-480 fallback is a number, so it is not
    // listed here. The trailing-resolution de-duplication in TemplateEngine reads this list.
    public static readonly IReadOnlyList<string> KnownLabels = ["4k", "1440p", "1080p", "720p", "480p"];

    // A height below 480 renders as the raw height with a "p" suffix, such as "368p". Libraries
    // label low-res files that way in their own filenames, so a bare number would rewrite an
    // already-correct "[368p]" down to "[368]".
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
