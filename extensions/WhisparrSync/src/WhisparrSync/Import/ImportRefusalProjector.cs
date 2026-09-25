using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Import;

/// <summary>
/// Folds one refusal, or one root's success, into the per-root aggregate the options blob carries.
/// </summary>
/// <remarks>
/// The output is one entry per root, each a count and at most
/// <see cref="ImportRootRefusals.NewestPathsKept"/> paths. That is a fixed size, not a cap on
/// something that grows.
/// <para>
/// Entries are keyed through <see cref="ImportRootRefusals.NormaliseRoot"/>, so two spellings of
/// one root that differ only by a trailing separator are one entry.
/// </para>
/// <para>
/// A success clears the root it came from and leaves every other entry as it was.
/// </para>
/// </remarks>
public static class ImportRefusalProjector
{
    /// <summary>The key a refusal is counted under when no root contained the reported path.</summary>
    /// <remarks>Blank, which no reported root normalises to.</remarks>
    public const string NoReportedRoot = "";

    /// <summary>
    /// <paramref name="current"/> with <paramref name="path"/> counted against
    /// <paramref name="root"/>, which is blank for <see cref="NoReportedRoot"/>.
    /// </summary>
    /// <remarks>
    /// Answers with an aggregate equal to <paramref name="current"/> when the fold changes nothing,
    /// which lets a caller skip a write on a delivery that added no information.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    public static List<ImportRootRefusals> Refuse(
        IReadOnlyList<ImportRootRefusals> current,
        string? root,
        string? path,
        ImportRefusalCause cause)
    {
        ArgumentNullException.ThrowIfNull(current);

        var key = ImportRootRefusals.NormaliseRoot(root);

        // Built before anything is compared, so both sides carry the entry's own length bound. A
        // path stored shortened would otherwise never match itself.
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
            // A path this root already lists neither lengthens the list nor counts again. Only its
            // cause can still change.
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
    /// <paramref name="current"/> with <paramref name="root"/>'s line cleared, blank for
    /// <see cref="NoReportedRoot"/>.
    /// </summary>
    /// <remarks>
    /// Answers with an aggregate equal to <paramref name="current"/> when that root has no line, so
    /// a working setup never writes the blob to say nothing changed.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="current"/> is null.</exception>
    public static List<ImportRootRefusals> Succeed(
        IReadOnlyList<ImportRootRefusals> current, string? root)
    {
        ArgumentNullException.ThrowIfNull(current);

        var key = ImportRootRefusals.NormaliseRoot(root);
        return [.. current.Where(entry => entry.Root != key)];
    }

    // The accumulator is the size the entry holds and stops when full, so the older paths are never
    // gathered into a longer collection first.
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

    // Saturates rather than overflowing.
    private static int Incremented(int count) => count == int.MaxValue ? count : count + 1;

    private static List<ImportRootRefusals> Replacing(
        IReadOnlyList<ImportRootRefusals> current, ImportRootRefusals replacement)
        => [.. current.Select(entry => entry.Root == replacement.Root ? replacement : entry)];
}
