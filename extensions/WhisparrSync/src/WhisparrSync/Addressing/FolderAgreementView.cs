using WhisparrSync.Options;

namespace WhisparrSync.Addressing;

/// <summary>One Cove library root a path or a refusal is stored for.</summary>
/// <remarks>
/// Refusal is what the last run left, and a line with no refusal always carries a Mapping, because
/// a root with neither has no line. PathsTried holds at most
/// <see cref="OutboundRootRefusal.PathsTriedKept"/> paths and is empty where nothing is refused.
/// Mapping can sit beside a refusal, because a mapped root can still be refused: the instance's
/// answer decides on every run.
/// </remarks>
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
/// count times <see cref="OutboundRootRefusal.PathsTriedKept"/>, a property of what is stored and
/// not of a truncation applied here.
/// <para>
/// A root with nothing stored either way has no line. A root a path is stored for keeps its line
/// once that path works, because the field that withdraws the path sits beside the line.
/// </para>
/// <para>Roots lists the refused roots in stored order, then the roots a path is stored for.</para>
/// </remarks>
public sealed record FolderAgreementView(IReadOnlyList<FolderAgreementRootLine> Roots)
{
    /// <summary>What <paramref name="refusals"/> and <paramref name="mappings"/> read as.</summary>
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
                // A folder under none of the library roots is refused with no root at all, and no
                // path can answer that, so it gets no line. The run's own report states it.
                .. refusals
                    .Where(entry => !string.IsNullOrEmpty(entry.Root))
                    .Select(entry => new FolderAgreementRootLine(
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
