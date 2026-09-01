using WhisparrSync.Options;

namespace WhisparrSync.Import;

/// <summary>One offending path, and why it was not imported.</summary>
/// <param name="Path">The path the delivery reported.</param>
/// <param name="Cause">
/// Why that path was refused. Named per path rather than per root, so a misconfigured root and one
/// unreadable file do not read identically.
/// </param>
public sealed record ImportBannerPathLine(string Path, ImportRefusalCause Cause);

/// <summary>One Whisparr root's outstanding refusals, as the settings page reads them.</summary>
/// <param name="Root">
/// The root as the reporting instance spells it. Blank where no reporting root contained the path,
/// which the surface names rather than dropping.
/// </param>
/// <param name="CountSinceLastSuccess">
/// How many refusals this root has had since its last successful import, as stored. Nothing on this
/// path derives a second figure from it.
/// </param>
/// <param name="NewestPaths">
/// The newest offending paths, newest first, at most
/// <see cref="ImportRootRefusals.NewestPathsKept"/> of them.
/// </param>
public sealed record ImportBannerRootLine(
    string Root,
    int CountSinceLastSuccess,
    IReadOnlyList<ImportBannerPathLine> NewestPaths);

/// <summary>
/// What Whisparr reported and Cove's library does not hold: the refusals outstanding, one line per
/// Whisparr root that has any, and the records the backstop could not take at all.
/// </summary>
/// <remarks>
/// A projection of the stored aggregates, never a live options type. Its size is the Whisparr root
/// count times <see cref="ImportRootRefusals.NewestPathsKept"/> plus two scalars, which is a property
/// of what is stored rather than of a truncation applied here.
/// <para>
/// The surface renders nothing for an answer with no roots and nothing contained.
/// </para>
/// </remarks>
/// <param name="Roots">One line per root with refusals outstanding, in the order they are stored.</param>
/// <param name="RecordsContained">
/// How many of Whisparr's history records the backstop could not take, over every pass. A running
/// total: the mark moved past each of them, so this channel never offers them again and no later
/// success clears the count.
/// </param>
/// <param name="LastContainedAtUtc">
/// When a pass last could not take a record, or null when none ever has.
/// </param>
public sealed record ImportBannerView(
    IReadOnlyList<ImportBannerRootLine> Roots,
    int RecordsContained,
    DateTimeOffset? LastContainedAtUtc)
{
    /// <summary>What <paramref name="refusals"/> and <paramref name="health"/> read as.</summary>
    /// <param name="refusals">The stored refusals, one entry per root that has any.</param>
    /// <param name="health">The stored import health, which carries the containment.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="refusals"/> or <paramref name="health"/> is null.
    /// </exception>
    public static ImportBannerView From(
        IReadOnlyList<ImportRootRefusals> refusals, ImportHealthAggregate health)
    {
        ArgumentNullException.ThrowIfNull(refusals);
        ArgumentNullException.ThrowIfNull(health);

        return new ImportBannerView(
            [
                .. refusals.Select(entry => new ImportBannerRootLine(
                    entry.Root,
                    entry.CountSinceLastSuccess,
                    [.. entry.NewestPaths.Select(path => new ImportBannerPathLine(path.Path, path.Cause))])),
            ],
            health.RecordsContained,
            health.LastContainedAtUtc);
    }
}
