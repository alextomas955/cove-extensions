using System.Text.Json.Serialization;
using Cove.Extensions.Shared;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Contracts;

/// <summary>What a caller may say when it asks for an entity to be monitored.</summary>
/// <remarks>
/// A scope and nothing else. There is no identifier member of any kind, so which entity the outbound
/// request touches is not expressible in the request at all rather than being a value a validation
/// step has to refuse: the route names the Cove entity, and the identifier the instance is given is
/// read from the stored identity row on the server.
/// </remarks>
/// <param name="Scope">
/// How much of the entity's catalogue to cover, or null to take the product's own default. An
/// unrecognised spelling fails to bind, so no value can arrive as a default the caller did not name.
/// </param>
public sealed record MonitorEntityRequest(MonitorScope? Scope);

/// <summary>Why a monitor could not be applied, or that it was.</summary>
/// <remarks>
/// One value per reason, never collapsed into a generic failure: each sends the user somewhere
/// different, and several are indistinguishable under the wrong test.
/// <para>
/// The backend answers with a KIND. The sentence a user reads is a frontend constant, so nothing an
/// instance said can reach the copy. That matters more here than elsewhere: this generation answers a
/// refused add with a body carrying a full stack trace.
/// </para>
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum MonitorRefusalKind
{
    /// <summary>Nothing was refused.</summary>
    None,

    /// <summary>No instance is configured, so no request was made.</summary>
    NotConfigured,

    /// <summary>
    /// The entity carries no identifier in the connected generation's own namespace, so there is
    /// nothing to name it by.
    /// </summary>
    NoIdentityInThisNamespace,

    /// <summary>The connected generation holds no capability that could honour this.</summary>
    CapabilityAbsentOnThisGeneration,

    /// <summary>The instance offers no quality profile, so no add can be composed.</summary>
    NoQualityProfile,

    /// <summary>The instance offers no library root, so no add can be composed.</summary>
    NoRootFolder,

    /// <summary>The instance answered, and would not do it.</summary>
    InstanceRefused,
}

/// <summary>What one entity's monitoring looks like, as the entity page reads it.</summary>
/// <remarks>
/// Discloses no API key and no part of any response body: only a classified kind and the named
/// values a sentence needs.
/// <para>
/// It carries no count of any sort. A freshly added entity reports a catalogue of zero before any
/// refresh has run, so a count here would be a confident zero this product cannot support.
/// </para>
/// </remarks>
/// <param name="Kind">Which kind of entity this is about.</param>
/// <param name="Generation">The connected generation, or null when none is configured.</param>
/// <param name="Monitored">Whether the connected instance monitors this entity.</param>
/// <param name="Refusal">Why the last thing asked for could not be done, or that it was done.</param>
/// <param name="Capabilities">
/// What the connected generation can do. The browser reads its menu from this rather than from a
/// generation table of its own, so a capability that is absent is refused in one place.
/// </param>
public sealed record EntityMonitoringView(
    WhisparrEntityKind Kind,
    WhisparrGeneration? Generation,
    bool Monitored,
    MonitorRefusalKind Refusal,
    IReadOnlyList<WhisparrCapability> Capabilities)
{
    /// <summary>A refusal taken before any instance was contacted, with nothing configured.</summary>
    public static EntityMonitoringView NotConfigured(WhisparrEntityKind kind)
        => new(kind, null, false, MonitorRefusalKind.NotConfigured, []);

    /// <summary>A refusal naming <paramref name="refusal"/>, with the entity left unmonitored.</summary>
    public static EntityMonitoringView Refused(
        WhisparrEntityKind kind,
        WhisparrGeneration generation,
        IReadOnlyList<WhisparrCapability> capabilities,
        MonitorRefusalKind refusal)
        => new(kind, generation, false, refusal, capabilities);

    /// <summary>The entity's state as the instance reports it.</summary>
    public static EntityMonitoringView State(
        WhisparrEntityKind kind,
        WhisparrGeneration generation,
        IReadOnlyList<WhisparrCapability> capabilities,
        bool monitored)
        => new(kind, generation, monitored, MonitorRefusalKind.None, capabilities);
}
