using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>The address and key one connection test is taken against.</summary>
/// <remarks>
/// The key travels IN, never out. Nothing on the response side carries it back, so a browser that
/// submitted one cannot read it again from any answer this extension gives.
/// </remarks>
/// <param name="Address">The base address of the Whisparr instance, as the operator typed it.</param>
/// <param name="ApiKey">The instance's API key.</param>
public sealed record ConnectionTestRequest(string? Address, string? ApiKey);

/// <summary>Which Whisparr generation answered.</summary>
/// <remarks>
/// The wire spelling is declared HERE, on the type. An equivalent converter in a serializer options
/// collection would outrank this one rather than duplicate it, so a second declaration could drift
/// and win in silence.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum WhisparrGeneration
{
    /// <summary>Whisparr v3, the Eros line.</summary>
    V3,

    /// <summary>Whisparr v2.</summary>
    V2,
}

/// <summary>Something a Whisparr generation can do.</summary>
/// <remarks>
/// Names what the connected instance CAN do rather than what it was checked for: a generation that
/// cannot honour one holds no role expressing it, so the capability is absent from the list rather
/// than present with a false beside it. The wire spelling is declared on the type.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum WhisparrCapability
{
    /// <summary>A callback secret can travel somewhere other than the address registered.</summary>
    OutOfBandCallbackSecret,

    /// <summary>A studio can be monitored, and the scope of a held one changed.</summary>
    MonitorStudio,

    /// <summary>A performer can be monitored. Only one generation addresses a performer at all.</summary>
    MonitorPerformer,

    /// <summary>A scene the instance's catalogue lacks can be registered without acquiring it.</summary>
    RegisterMissingScenes,

    /// <summary>A file the library already holds can be attached without its data being transferred.</summary>
    ReflectOwnedFiles,

    /// <summary>
    /// The instance can be asked to look for what it monitors and does not hold. The one capability
    /// here that acquires anything.
    /// </summary>
    SearchMonitored,
}

/// <summary>
/// What one connection attempt turned out to be. One value per step of the decision table.
/// </summary>
/// <remarks>
/// The four refusals are distinct values on purpose and must never be collapsed into one generic
/// failure: each sends the user somewhere different, and three of the four are indistinguishable
/// under the wrong test.
/// <para>
/// The backend answers with a KIND. The sentence a user reads is a frontend constant, so no server
/// value can reach the copy except through a named field on the view.
/// </para>
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum ConnectionFailureKind
{
    /// <summary>An instance this product manages answered. Not a refusal.</summary>
    Connected,

    /// <summary>The address or the key was not supplied, so no request was made.</summary>
    NotConfigured,

    /// <summary>Nothing answered.</summary>
    Unreachable,

    /// <summary>Something answered and rejected the key.</summary>
    KeyRejected,

    /// <summary>Something answered, but not as the Whisparr API.</summary>
    NotTheWhisparrApi,

    /// <summary>The Whisparr API answered, on a version this product does not manage.</summary>
    VersionNotManaged,
}

/// <summary>A setting a connection cannot be attempted without.</summary>
/// <remarks>
/// Named on a refusal so the sentence points at the field that is actually empty rather than at the
/// pair. The wire spelling is declared on the type.
/// </remarks>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum ConnectionSetting
{
    /// <summary>The instance's base address.</summary>
    Address,

    /// <summary>The instance's API key.</summary>
    ApiKey,
}

/// <summary>The result of one connection test, as the settings page reads it.</summary>
/// <remarks>
/// Discloses no API key and no response body: only a classified kind and the named values a sentence
/// needs. That is what keeps the route from working as a request-forwarding oracle — a caller who
/// aims it at an internal address learns which of six kinds applied, never what answered.
/// </remarks>
/// <param name="Kind">Which step of the decision table this attempt reached.</param>
/// <param name="Generation">The generation detected, or null unless the attempt connected.</param>
/// <param name="Capabilities">
/// What the connected generation can do, or null unless the attempt connected. A capability absent
/// from this list is a GENERATION gap — that generation has no such thing, on any build of it.
/// <paramref name="Corroborated"/> reports the other finding, a build disagreeing with itself.
/// </param>
/// <param name="Version">
/// The instance's own version string, character for character as it sent it. Present on a success and
/// on a refusal that names the version found.
/// </param>
/// <param name="Branch">The branch the instance named, when it named one.</param>
/// <param name="Corroborated">
/// Whether the branch and count-field readings agree with the detected generation, or null when
/// there was no generation to corroborate. False is a build-gap finding, not a generation gap.
/// </param>
/// <param name="OtherApplication">
/// The <c>appName</c> received when it was not this product's, so the refusal names what actually
/// answered rather than a value from a table of applications this code knows about.
/// </param>
/// <param name="Address">
/// The address the attempt was made against, with any credentials in it removed, so a result can be
/// matched to the request that produced it.
/// </param>
/// <param name="MissingSetting">
/// Which setting was empty when nothing was configured, or null on any other kind. When both are
/// empty this names the address, so two runs of the same refusal read the same.
/// </param>
public sealed record ConnectionTestView(
    ConnectionFailureKind Kind,
    WhisparrGeneration? Generation,
    IReadOnlyList<WhisparrCapability>? Capabilities,
    string? Version,
    string? Branch,
    bool? Corroborated,
    string? OtherApplication,
    string? Address,
    ConnectionSetting? MissingSetting)
{
    /// <summary>A refusal taken before any request was made, naming the setting that is empty.</summary>
    public static ConnectionTestView NotConfigured(ConnectionSetting missing, string? address)
        => new(ConnectionFailureKind.NotConfigured, null, null, null, null, null, null, address, missing);
}
