namespace Renamer.Planner;

/// <summary>
/// The three groups a dry-run row falls into for filtering and for the summary's headline counts.
/// </summary>
public enum ScanBucketKind
{
    /// <summary>The file would be renamed or moved.</summary>
    WillChange,

    /// <summary>The file would be skipped, blocked or failed — the rows a user must look at.</summary>
    Attention,

    /// <summary>The file is already where the template puts it.</summary>
    NoChange,
}

/// <summary>Maps a <see cref="RenamerStatus"/> to the bucket the dry run groups it under.</summary>
public static class ScanBucket
{
    /// <summary>The bucket <paramref name="status"/> belongs to.</summary>
    /// <remarks>
    /// Anything that is not an acting status or a no-op buckets as <see cref="ScanBucketKind.Attention"/>,
    /// including a status added after this method was written — a new skip reason must surface for review,
    /// never be hidden or throw, so the default arm catches it deliberately.
    /// </remarks>
    public static ScanBucketKind Of(RenamerStatus status) => status switch
    {
        RenamerStatus.Renamer or RenamerStatus.Move => ScanBucketKind.WillChange,
        RenamerStatus.NoOp => ScanBucketKind.NoChange,
        _ => ScanBucketKind.Attention,
    };

    /// <summary>
    /// Parses a wire bucket name; <c>null</c>, blank and <c>all</c> all yield a null
    /// <paramref name="bucket"/>, meaning no filter.
    /// </summary>
    /// <returns>False iff <paramref name="wire"/> is a non-blank value that names no bucket.</returns>
    public static bool TryParse(string? wire, out ScanBucketKind? bucket)
    {
        bucket = null;
        if (string.IsNullOrWhiteSpace(wire) || wire.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Enum.TryParse<ScanBucketKind>(wire, ignoreCase: true, out var parsed))
        {
            bucket = parsed;
            return true;
        }

        return false;
    }
}
