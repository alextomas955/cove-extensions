namespace Renamer.Contracts;

/// <summary>One kind's file counts from a completed whole-library rename.</summary>
/// <remarks>
/// <c>Skipped</c> counts every file the run did not rename or fail: those the planner skipped and
/// those skipped at move time. A file already named correctly is in none of the three counts.
/// <c>StoppedForSpace</c> is set when a destination volume ran out of room and this kind stopped
/// early; the counts then cover only what ran before the stop.
/// </remarks>
public sealed record LibraryRenameKindTally(
    RenamerFileKind Kind,
    int Renamed,
    int Skipped,
    int Failed,
    bool StoppedForSpace);

/// <summary>What a completed whole-library rename persists: per-kind file counts.</summary>
/// <remarks>
/// Nothing per file is stored, so the value's size depends on the kind count alone. The figures stay
/// split per kind so the readback can drop the kinds the caller may not see. <c>Kinds</c> holds one
/// entry per kind that had at least one entity.
/// </remarks>
public sealed record LibraryRenameSummary(
    int SchemaVersion,
    long CompletedAtUtcTicks,
    IReadOnlyList<LibraryRenameKindTally> Kinds)
{
    // The readback 404s on a stamp it does not recognise, so a later reshape reads as "no run yet".
    public const int CurrentSchemaVersion = 1;
}

/// <summary>
/// What <c>/last-library-rename</c> returns: the stored <see cref="LibraryRenameSummary"/> summed over
/// the kinds the calling principal may read.
/// </summary>
/// <remarks>
/// <c>Kinds</c> names the kinds the counts cover, and <c>StoppedForSpace</c> the subset of them that
/// stopped early because a destination volume ran out of room.
/// </remarks>
public sealed record LibraryRenameSummaryView(
    int Renamed,
    int Skipped,
    int Failed,
    IReadOnlyList<RenamerFileKind> StoppedForSpace,
    long CompletedAtUtcTicks,
    IReadOnlyList<RenamerFileKind> Kinds)
{
    /// <summary>Sums <paramref name="summary"/> over <paramref name="readableKinds"/>.</summary>
    public static LibraryRenameSummaryView From(
        LibraryRenameSummary summary, IReadOnlyList<RenamerFileKind> readableKinds)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(readableKinds);

        var kinds = summary.Kinds.Where(k => readableKinds.Contains(k.Kind)).ToList();
        return new LibraryRenameSummaryView(
            Renamed: kinds.Sum(k => k.Renamed),
            Skipped: kinds.Sum(k => k.Skipped),
            Failed: kinds.Sum(k => k.Failed),
            StoppedForSpace: [.. kinds.Where(k => k.StoppedForSpace).Select(k => k.Kind)],
            CompletedAtUtcTicks: summary.CompletedAtUtcTicks,
            Kinds: [.. kinds.Select(k => k.Kind)]);
    }
}
