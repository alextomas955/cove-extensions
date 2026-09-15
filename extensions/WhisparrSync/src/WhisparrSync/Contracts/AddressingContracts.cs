using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using WhisparrSync.Addressing;

namespace WhisparrSync.Contracts;

/// <summary>What a save of one library root's mapping came to.</summary>
/// <remarks>
/// The wire spelling is declared HERE, on the type. An equivalent converter in a serializer options
/// collection would outrank this one rather than duplicate it.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum FolderMappingSaveOutcome
{
    /// <summary>The mapping is stored.</summary>
    /// <remarks>
    /// Answered only where a probe run with the supplied path resolved the root under it. A mapping
    /// is never stored on the strength of having been typed.
    /// </remarks>
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
/// paths, because a mapping for a path Cove holds nothing under could never be probed.
/// </param>
/// <param name="InstancePath">
/// Where the instance holds that root. Blank removes the mapping already stored for it.
/// </param>
public sealed record FolderMappingSaveRequest(string CoveRoot, string InstancePath);

/// <summary>What one save came to, and what the instance was asked about.</summary>
/// <param name="Outcome">What the save did.</param>
/// <param name="Refusal">
/// Why the root did not resolve, or null where nothing was refused. Present beside
/// <see cref="FolderMappingSaveOutcome.Refused"/> so the reader is told which refusal it was rather
/// than only that the path did not work.
/// </param>
/// <param name="Tried">
/// The paths the instance was asked about, which is the path built from the supplied mapping where
/// one was probed and empty where none was.
/// </param>
public sealed record FolderMappingSaveResult(
    FolderMappingSaveOutcome Outcome,
    FolderAgreementRefusal? Refusal,
    IReadOnlyList<string> Tried);
