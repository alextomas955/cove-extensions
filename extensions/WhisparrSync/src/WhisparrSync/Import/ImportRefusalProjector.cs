using WhisparrSync.Options;

namespace WhisparrSync.Import;

/// <summary>
/// Folds one refusal, or one root's success, into the per-root aggregate the options blob carries.
/// </summary>
/// <remarks>
/// The fold's output is a handful of entries, each a count and at most
/// <see cref="ImportRootRefusals.NewestPathsKept"/> paths. Three paths is a fixed-size design, not a
/// cap on something that grows.
/// <para>
/// Entries are keyed through <see cref="ImportRootRefusals.NormaliseRoot"/>, so two spellings of one
/// root that differ only by a trailing separator are one entry.
/// </para>
/// <para>
/// A success clears the root it came from and leaves every other entry as it was, so a setup where
/// one root works and another does not keeps the failing root's line.
/// </para>
/// </remarks>
public static class ImportRefusalProjector
{
    /// <summary>The key a refusal is counted under when no root contained the reported path.</summary>
    /// <remarks>
    /// Blank, which no reported root normalises to. A delivery naming a path that falls under none of
    /// the reporting instance's own roots is the case the banner exists to make visible, so it is
    /// counted rather than dropped.
    /// </remarks>
    public const string NoReportedRoot = "";

    /// <summary>
    /// <paramref name="current"/> with <paramref name="path"/> counted against
    /// <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Answers with an aggregate equal to <paramref name="current"/> when the fold changes nothing,
    /// which is what lets a caller skip a write on a delivery that added no information.
    /// </remarks>
    /// <param name="current">The aggregate as stored.</param>
    /// <param name="root">The root to count under; blank for <see cref="NoReportedRoot"/>.</param>
    /// <param name="path">The offending path, as the delivery reported it.</param>
    /// <param name="cause">Why that path was refused.</param>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    public static List<ImportRootRefusals> Refuse(
        IReadOnlyList<ImportRootRefusals> current,
        string? root,
        string? path,
        ImportRefusalCause cause)
    {
        ArgumentNullException.ThrowIfNull(current);

        var key = ImportRootRefusals.NormaliseRoot(root);

        // Built before anything is compared, so both sides of the comparison below carry the same
        // length bound. A path stored shortened would otherwise never match itself.
        var refusal = new ImportRefusalEntry { Path = path ?? "", Cause = cause };
        var existing = current.FirstOrDefault(entry => entry.Root == key);

        if (existing is null)
        {
            return [.. current, new ImportRootRefusals
            {
                Root = key,
                CountSinceLastSuccess = 1,
                NewestPaths = [refusal],
            }];
        }

        var listed = existing.NewestPaths.FirstOrDefault(entry => entry.Path == refusal.Path);
        if (listed is not null)
        {
            // A path this root already lists is already reported, so the delivery neither lengthens
            // the list nor counts again. Only its cause can still change.
            return listed.Cause == cause
                ? [.. current]
                : Replacing(current, existing with
                {
                    NewestPaths = [.. existing.NewestPaths.Select(entry => entry.Path == refusal.Path
                        ? entry with { Cause = cause }
                        : entry)],
                });
        }

        return Replacing(current, existing with
        {
            CountSinceLastSuccess = Incremented(existing.CountSinceLastSuccess),
            NewestPaths = NewestFirst(refusal, existing),
        });
    }

    /// <summary>
    /// <paramref name="current"/> with <paramref name="root"/>'s line cleared.
    /// </summary>
    /// <remarks>
    /// Answers with an aggregate equal to <paramref name="current"/> when that root has no line, so a
    /// working setup never writes the blob to say nothing changed.
    /// </remarks>
    /// <param name="current">The aggregate as stored.</param>
    /// <param name="root">The root whose import succeeded; blank for <see cref="NoReportedRoot"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    public static List<ImportRootRefusals> Succeed(
        IReadOnlyList<ImportRootRefusals> current, string? root)
    {
        ArgumentNullException.ThrowIfNull(current);

        var key = ImportRootRefusals.NormaliseRoot(root);
        return [.. current.Where(entry => entry.Root != key)];
    }

    /// <summary>
    /// <paramref name="newest"/> ahead of as many of <paramref name="existing"/>'s paths as the
    /// fixed size leaves room for.
    /// </summary>
    /// <remarks>
    /// The accumulator is the size the entry holds, and it stops when it is full: the older paths are
    /// never gathered into a longer collection first.
    /// </remarks>
    private static List<ImportRefusalEntry> NewestFirst(
        ImportRefusalEntry newest, ImportRootRefusals existing)
    {
        var kept = new List<ImportRefusalEntry>(ImportRootRefusals.NewestPathsKept) { newest };
        foreach (var older in existing.NewestPaths)
        {
            if (kept.Count == ImportRootRefusals.NewestPathsKept)
            {
                break;
            }

            kept.Add(older);
        }

        return kept;
    }

    /// <summary>One more refusal, or the same count once there is no larger one.</summary>
    private static int Incremented(int count) => count == int.MaxValue ? count : count + 1;

    private static List<ImportRootRefusals> Replacing(
        IReadOnlyList<ImportRootRefusals> current, ImportRootRefusals replacement)
        => [.. current.Select(entry => entry.Root == replacement.Root ? replacement : entry)];
}
