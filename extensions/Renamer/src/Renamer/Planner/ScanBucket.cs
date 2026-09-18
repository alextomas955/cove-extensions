namespace Renamer.Planner;

// The three groups a dry-run row falls into for filtering and for the summary's headline counts.
public enum ScanBucketKind
{
    WillChange,

    // The rows a user must look at: skipped, blocked or failed.
    Attention,

    NoChange,
}

public static class ScanBucket
{
    // Anything that is neither an acting status nor a no-op buckets as Attention, including a status
    // added after this method was written: a new skip reason must surface for review, never be hidden or
    // throw, so the default arm catches it deliberately.
    public static ScanBucketKind Of(RenamerStatus status) => status switch
    {
        RenamerStatus.Renamer or RenamerStatus.Move => ScanBucketKind.WillChange,
        RenamerStatus.NoOp => ScanBucketKind.NoChange,
        _ => ScanBucketKind.Attention,
    };

    // Parses a wire bucket name. Null, blank and "all" all yield a null bucket, meaning no filter.
    // Returns false only for a non-blank value that names no bucket.
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
