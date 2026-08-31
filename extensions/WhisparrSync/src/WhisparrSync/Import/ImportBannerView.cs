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

/// <summary>The refusals outstanding, one line per Whisparr root that has any.</summary>
/// <remarks>
/// A projection of the stored aggregate, never a live options type. Its size is the Whisparr root
/// count times <see cref="ImportRootRefusals.NewestPathsKept"/>, which is a property of what is
/// stored rather than of a truncation applied here.
/// <para>
/// Empty while every root's last import succeeded. The surface renders nothing for an empty answer.
/// </para>
/// </remarks>
/// <param name="Roots">One line per root with refusals outstanding, in the order they are stored.</param>
public sealed record ImportBannerView(IReadOnlyList<ImportBannerRootLine> Roots)
{
    /// <summary><paramref name="refusals"/> as this surface reads it.</summary>
    /// <param name="refusals">The stored aggregate.</param>
    /// <exception cref="ArgumentNullException"><paramref name="refusals"/> is null.</exception>
    public static ImportBannerView From(IReadOnlyList<ImportRootRefusals> refusals)
    {
        ArgumentNullException.ThrowIfNull(refusals);

        return new ImportBannerView(
        [
            .. refusals.Select(entry => new ImportBannerRootLine(
                entry.Root,
                entry.CountSinceLastSuccess,
                [.. entry.NewestPaths.Select(path => new ImportBannerPathLine(path.Path, path.Cause))])),
        ]);
    }
}
