using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Import;

/// <summary>Why a reported file was not imported.</summary>
/// <remarks>
/// Three values rather than a boolean: a misconfigured root and one bad file must not read
/// identically to a user. The wire spelling is declared on the type; an equivalent converter in a
/// serializer options collection would outrank it rather than duplicate it.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum ImportRefusalCause
{
    /// <summary>The reported file was found under no Cove library root.</summary>
    NotFoundUnderAnyRoot,

    /// <summary>Two or more candidates verified, so none was chosen.</summary>
    AmbiguousCandidates,

    /// <summary>The verified path could not be read by the host.</summary>
    Unreadable,
}
