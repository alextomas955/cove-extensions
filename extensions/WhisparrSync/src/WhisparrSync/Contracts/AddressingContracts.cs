using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using WhisparrSync.Addressing;

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
