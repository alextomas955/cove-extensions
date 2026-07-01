namespace Renamer.Tests;

/// <summary>
/// Shared, deterministic fixture data for the deliberately-long template + metadata case.
///
/// The LengthReducer test consumes this: the rendered name is engineered to exceed
/// 255 chars so the reducer must walk every default drop-order field
/// (videoCodec → audioCodec → frameRate → resolution → tags → studioCode → studio →
/// performers → date) and finally hard-truncate the <c>$title</c> component.
///
/// This class has no test methods of its own — it is shared data.
/// </summary>
internal static class LongTemplateFixture
{
    /// <summary>
    /// A filename template referencing many tokens, including the multi-value
    /// <c>$performers</c>/<c>$tags</c> fields and every default drop-order field.
    /// </summary>
    public const string FilenameTemplate =
        "$studio - $studioCode - $title - $performers " +
        "[$resolution $videoCodec $audioCodec $frameRate] {$tags} ($date)";

    /// <summary>
    /// A deliberately-long title (200+ chars) so that, even after every drop-order
    /// field is removed, the title alone still exceeds the 255-char filename cap and
    /// forces the hard-truncate last resort.
    /// </summary>
    public const string LongTitle =
        "The Exceedingly Verbose And Deliberately Overlong Documentary Title That Keeps " +
        "Going Well Past Any Reasonable Filesystem Component Length Limit In Order To " +
        "Exercise Every Single Field Drop And Then The Final Hard Truncate Of The Title " +
        "Itself So That Even With Every Other Drop-Order Field Removed The Bare Title " +
        "Alone Still Exceeds Two Hundred And Fifty Five Characters And Must Be Cut Short";

    /// <summary>
    /// Scalar token values. Performers/tags are supplied separately via
    /// <see cref="Performers"/> / <see cref="Tags"/> for multi-value resolution.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Tokens { get; } = new Dictionary<string, string>
    {
        ["title"] = LongTitle,
        ["studio"] = "Some Very Long Studio Production Company Name International",
        ["studioCode"] = "STUDIOCODE-0000000001",
        ["resolution"] = "2160p",
        ["videoCodec"] = "h265-hevc-main10",
        ["audioCodec"] = "truehd-atmos-7point1",
        ["frameRate"] = "59.94fps",
        ["date"] = "2026-06-27",
        ["ext"] = "mkv",
    };

    /// <summary>Many long performer names so the joined value is itself substantial.</summary>
    public static IReadOnlyList<string> Performers { get; } =
    [
        "Alexandria Featherstonehaugh",
        "Bartholomew Fitzgerald-Montgomery",
        "Cassandra Wollstonecraft-Bennett",
        "Demetrius Aurelius Constantinopoulos",
        "Evangeline Marchetti-Hawthorne",
    ];

    /// <summary>Many long tag values.</summary>
    public static IReadOnlyList<string> Tags { get; } =
    [
        "documentary-feature-length",
        "remastered-restoration-4k",
        "criterion-collection-edition",
        "behind-the-scenes-commentary",
        "extended-directors-cut",
    ];
}
