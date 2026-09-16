using WhisparrSync.Options;

namespace WhisparrSync.Addressing;

/// <summary>One Cove library root a path or a refusal is stored for.</summary>
/// <param name="Root">The library root, as the host is configured with it.</param>
/// <param name="Refusal">
/// What that root could not establish, as the last run left it, or null where nothing is refused. A
/// line with no refusal always carries a <paramref name="Mapping"/>, because a root with neither has
/// no line.
/// </param>
/// <param name="PathsTried">
/// The paths the instance was asked about, at most
/// <see cref="OutboundRootRefusal.PathsTriedKept"/> of them, and empty where nothing is refused.
/// </param>
/// <param name="Mapping">
/// Where an operator has stated the instance holds this root, or null where none is stored. Present
/// beside a refusal because a mapped root can still be refused: the instance's answer decides on
/// every run.
/// </param>
public sealed record FolderAgreementRootLine(
    string Root,
    FolderAgreementRefusal? Refusal,
    IReadOnlyList<string> PathsTried,
    string? Mapping);

/// <summary>
/// The Cove library roots a path or a refusal is stored for, as the settings page reads them.
/// </summary>
/// <remarks>
/// A projection of the stored aggregates, never a live options type. Its size is the library root
/// count times <see cref="OutboundRootRefusal.PathsTriedKept"/>, which is a property of what is
/// stored rather than of a truncation applied here.
/// <para>
/// A root with nothing stored either way has no line, so a fixed configuration with no path supplied
/// for it stops asking. A root a path is stored for keeps its line once that path works, because the
/// field that withdraws the path is offered beside the line.
/// </para>
/// </remarks>
/// <param name="Roots">
/// The refused roots in the order they are stored, then the remaining roots a path is stored for.
/// </param>
public sealed record FolderAgreementView(IReadOnlyList<FolderAgreementRootLine> Roots)
{
    /// <summary>What <paramref name="refusals"/> and <paramref name="mappings"/> read as.</summary>
    /// <param name="refusals">The stored refusals, one entry per root that has one.</param>
    /// <param name="mappings">The stored mappings, one entry per root an operator supplied a path for.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="refusals"/> or <paramref name="mappings"/> is null.
    /// </exception>
    public static FolderAgreementView From(
        IReadOnlyList<OutboundRootRefusal> refusals, IReadOnlyList<OutboundRootMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(refusals);
        ArgumentNullException.ThrowIfNull(mappings);

        var refused = new HashSet<string>(
            refusals.Select(entry => ImportRootRefusals.NormaliseRoot(entry.Root)),
            StringComparer.Ordinal);

        return new FolderAgreementView(
            [
                .. refusals.Select(entry => new FolderAgreementRootLine(
                    entry.Root,
                    entry.Refusal,
                    [.. entry.PathsTried],
                    OutboundRefusalProjector.MappingFor(mappings, entry.Root))),
                .. mappings
                    .Where(entry => !refused.Contains(ImportRootRefusals.NormaliseRoot(entry.CoveRoot)))
                    .Select(entry => new FolderAgreementRootLine(
                        entry.CoveRoot, Refusal: null, PathsTried: [], entry.InstanceRoot)),
            ]);
    }
}
