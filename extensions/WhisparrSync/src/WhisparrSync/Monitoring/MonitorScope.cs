using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Monitoring;

/// <summary>How much of an entity's catalogue a monitor covers.</summary>
/// <remarks>
/// Whisparr's own two names, spelled the same way on both generations. The wire spelling is declared
/// HERE, on the type. An equivalent converter in a serializer options collection would outrank this
/// one rather than duplicate it, so a second declaration could drift and win in silence.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MonitorScope
{
    /// <summary>Future Scenes: monitor scenes that have not released yet.</summary>
    FutureScenes,

    /// <summary>All Scenes: monitor all scenes except specials.</summary>
    AllScenes,
}

/// <summary>Which kind of entity a monitor names.</summary>
/// <remarks>
/// The two generations address these kinds in namespaces neither shares with the other, so the kind
/// is carried beside an identifier rather than read out of one. The wire spelling is declared on the
/// type.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum WhisparrEntityKind
{
    Studio,
    Performer,
}

/// <summary>The instance-side values an add cannot be composed without.</summary>
/// <remarks>
/// Read from the instance at action time and handed to the acting member, so no acting member reads
/// anything for itself and there is no member a caller could aim at a value the instance never
/// offered.
/// </remarks>
public sealed record AddDefaults(int QualityProfileId, string RootFolderPath);
