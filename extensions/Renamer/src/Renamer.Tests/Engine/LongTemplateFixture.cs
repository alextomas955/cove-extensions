namespace Renamer.Tests.Engine;

internal static class LongTemplateFixture
{
    // A filename template referencing many tokens, including the multi-value $performers/$tags
    // fields and every default drop-order field.
    public const string FilenameTemplate =
        "$studio - $studioCode - $title - $performers " +
        "[$resolution $videoCodec $audioCodec $frameRate] {$tags} ($date)";

    // A deliberately-long title (200+ chars) so that, even after every drop-order field is removed,
    // the title alone still exceeds the 255-char filename cap and forces the hard-truncate last
    // resort.
    public const string LongTitle =
        "The Exceedingly Verbose And Deliberately Overlong Documentary Title That Keeps " +
        "Going Well Past Any Reasonable Filesystem Component Length Limit In Order To " +
        "Exercise Every Single Field Drop And Then The Final Hard Truncate Of The Title " +
        "Itself So That Even With Every Other Drop-Order Field Removed The Bare Title " +
        "Alone Still Exceeds Two Hundred And Fifty Five Characters And Must Be Cut Short";

    // Scalar token values. Performers/tags are supplied separately via Performers / Tags for
    // multi-value resolution.
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

    // Many long performer names so the joined value is itself substantial.
    public static IReadOnlyList<string> Performers { get; } =
    [
        "Alexandria Featherstonehaugh",
        "Bartholomew Fitzgerald-Montgomery",
        "Cassandra Wollstonecraft-Bennett",
        "Demetrius Aurelius Constantinopoulos",
        "Evangeline Marchetti-Hawthorne",
    ];

    // Many long tag values.
    public static IReadOnlyList<string> Tags { get; } =
    [
        "documentary-feature-length",
        "remastered-restoration-4k",
        "criterion-collection-edition",
        "behind-the-scenes-commentary",
        "extended-directors-cut",
    ];
}
