namespace Renamer.Engine;

// Cove's own resolution table, transcribed from ui/src/utils/resolutionBuckets.ts so a label
// written into a filename reads the same as the badge Cove shows on the item. The bucketing is
// fixed, not configurable.
public static class ResolutionLabel
{
    // A frame a little short of a standard resolution still carries that resolution's label.
    private const double LabelMargin = 0.95;

    // Indexed by the long edge. The ranges are contiguous, so the top row covers everything above it.
    private static readonly (string Label, int Value, int MinDimension, int MaxDimensionExclusive)[] Buckets =
    [
        ("144p", 144, 144, 341),
        ("240p", 240, 341, 533),
        ("360p", 360, 533, 747),
        ("480p", 480, 747, 907),
        ("540p", 540, 907, 1120),
        ("720p", 720, 1120, 1600),
        ("1080p", 1080, 1600, 2240),
        ("1440p", 1440, 2240, 3200),
        ("4K", 2160, 3200, 4480),
        ("5K", 2880, 4480, 5632),
        ("6K", 3384, 5632, 6656),
        ("7K", 4032, 6656, 7424),
        ("8K", 4320, 7424, 9840),
        ("HUGE", 9999, 9840, int.MaxValue),
    ];

    // Matched against the short edge: every bucket except the unbounded top one, which names no
    // standard resolution.
    private static readonly (string Label, int Value)[] StandardLabels =
        Buckets.Where(b => b.MaxDimensionExclusive != int.MaxValue)
            .Select(b => (b.Label, b.Value))
            .ToArray();

    // Every label FromDimensions can emit. The trailing-resolution de-duplication in TemplateEngine
    // reads this list.
    public static readonly IReadOnlyList<string> KnownLabels =
        Buckets.Select(b => b.Label).Union(StandardLabels.Select(s => s.Label), StringComparer.Ordinal).ToArray();

    // The larger of the two labels wins, so a portrait file reads the same as the landscape file of
    // the same shape while a very wide frame keeps its long-edge label. A frame renders empty only
    // when its long edge falls below the smallest bucket and its short edge below the smallest
    // standard label's margin.
    public static string FromDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return string.Empty;
        }

        int bucket = FindBucket(Math.Max(width, height));
        int standard = FindStandardLabel(Math.Min(width, height));

        if (standard < 0)
        {
            return bucket < 0 ? string.Empty : Buckets[bucket].Label;
        }

        if (bucket < 0 || StandardLabels[standard].Value > Buckets[bucket].Value)
        {
            return StandardLabels[standard].Label;
        }

        return Buckets[bucket].Label;
    }

    private static int FindBucket(int longEdge)
    {
        if (longEdge < Buckets[0].MinDimension)
        {
            return -1;
        }

        for (int i = 0; i < Buckets.Length; i++)
        {
            if (longEdge >= Buckets[i].MinDimension && longEdge < Buckets[i].MaxDimensionExclusive)
            {
                return i;
            }
        }

        return Buckets.Length - 1;
    }

    private static int FindStandardLabel(int shortEdge)
    {
        for (int i = StandardLabels.Length - 1; i >= 0; i--)
        {
            if (shortEdge >= StandardLabels[i].Value * LabelMargin)
            {
                return i;
            }
        }

        return -1;
    }
}
