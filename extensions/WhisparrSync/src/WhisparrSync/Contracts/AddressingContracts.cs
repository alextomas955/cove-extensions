using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using WhisparrSync.Addressing;
using WhisparrSync.Options;

namespace WhisparrSync.Contracts;

/// <summary>What a save of one library root's mapping came to.</summary>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum FolderMappingSaveOutcome
{
    /// <summary>
    /// The mapping is stored. Answered only where a probe run with the supplied path resolved the
    /// root under it.
    /// </summary>
    Stored,

    /// <summary>The stored mapping for that root was removed.</summary>
    Removed,

    /// <summary>
    /// A probe ran with the supplied path and the root did not resolve under it, so nothing was
    /// stored.
    /// </summary>
    Refused,

    /// <summary>The path named is none of the host's configured library paths.</summary>
    NotALibraryRoot,

    /// <summary>No instance is configured to ask, so nothing could be established either way.</summary>
    NotConfigured,
}

/// <summary>Where an operator states one Cove library root is on the connected instance.</summary>
/// <param name="CoveRoot">
/// The library root the mapping is for. Refused unless it is one of the host's configured library
/// paths.
/// </param>
/// <param name="InstancePath">
/// Where the instance holds that root. Blank removes the mapping already stored for it.
/// </param>
public sealed record FolderMappingSaveRequest(string CoveRoot, string InstancePath);

/// <summary>What one save came to, and what the instance was asked about.</summary>
/// <remarks>
/// <c>Refusal</c> says why the root did not resolve, and is null where nothing was refused.
/// <c>Tried</c> holds the paths the instance was asked about, and is empty where no probe ran.
/// </remarks>
public sealed record FolderMappingSaveResult(
    FolderMappingSaveOutcome Outcome,
    FolderAgreementRefusal? Refusal,
    IReadOnlyList<string> Tried);

/// <summary>Why one Cove library root has no agreed spelling on the connected instance.</summary>
/// <remarks>
/// Stored in the options blob and served to the settings page, so the wire spelling is declared on
/// the type. An equivalent converter on a serializer options object would outrank this one.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum FolderAgreementRefusal
{
    /// <summary>The instance declares no root to rebuild a tail under.</summary>
    InstanceDeclaresNoRoot,

    /// <summary>The library holds no file under this root to establish an agreement from.</summary>
    NoFileToProbeWith,

    /// <summary>No candidate held a file of the size the library holds.</summary>
    NothingResolved,

    /// <summary>Several candidates held one, so which of them the root means is not established.</summary>
    MoreThanOneResolved,

    /// <summary>
    /// The instance was asked and its answer could not be read, so nothing was established either
    /// way. Distinct from <see cref="NothingResolved"/>, which is an answer.
    /// </summary>
    ProbeCouldNotBeRead,

    /// <summary>The connected generation holds no role to ask the instance through.</summary>
    InstanceCannotBeAsked,

    /// <summary>The folder sits under none of the host's configured library roots.</summary>
    FolderUnderNoLibraryRoot,
}

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
