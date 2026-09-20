using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>The address and key one connection test is taken against.</summary>
/// <remarks>
/// The key travels in, never out. Nothing on the response side carries it back, so a browser that
/// submitted one cannot read it again from any answer this extension gives.
/// </remarks>
public sealed record ConnectionTestRequest(string? Address, string? ApiKey);

/// <summary>Which Whisparr generation answered.</summary>
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum WhisparrGeneration
{
    /// <summary>Whisparr v3, the Eros line.</summary>
    V3,

    V2,
}

/// <summary>Something a Whisparr generation can do.</summary>
/// <remarks>
/// A generation that cannot honour a capability holds no role expressing it, so the capability is
/// absent from the list rather than present with a false beside it.
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
    /// The instance can be asked to look for what it monitors and does not hold. Acquires content.
    /// </summary>
    SearchMonitored,

    /// <summary>What the instance holds for one catalogue scene can be read.</summary>
    ReadSceneStatus,

    /// <summary>
    /// Which scenes the instance's user has excluded can be read. A generation keeping no scene
    /// records keeps no scene exclusions either.
    /// </summary>
    ReadSceneExclusions,

    /// <summary>
    /// The instance can be asked to look for one catalogue scene it holds. Acquires content.
    /// </summary>
    SearchScene,

    /// <summary>One scene the instance holds can be monitored, and an unmonitored one left alone.</summary>
    MonitorScene,

    /// <summary>One scene can be excluded from what the instance takes, and the exclusion removed.</summary>
    ExcludeScene,

    /// <summary>
    /// A site the library's own scenes came from can be registered, monitoring nothing. Held by the
    /// generation whose unit of presence is a site rather than a scene.
    /// </summary>
    RegisterOwnedSites,

    /// <summary>
    /// Which of a set of scenes one site the instance holds has a row for can be read. Held by the
    /// generation that names a scene only as a row under a site.
    /// </summary>
    ReadSiteSceneRows,

    /// <summary>
    /// Which of a set of sites the instance holds can be read in one request. Held by the generation
    /// that answers presence for a site through its own list and by no other route.
    /// </summary>
    ReadHeldSites,

    /// <summary>
    /// What the instance holds at a path on its own filesystem can be read. Lets a caller establish
    /// which spelling of a folder the instance can open, rather than assuming the library's own.
    /// </summary>
    ReadInstanceFilesystem,
}

/// <summary>What one connection attempt turned out to be.</summary>
/// <remarks>
/// The four refusals are distinct values on purpose and must never be collapsed into one generic
/// failure: each sends the user somewhere different. The backend answers with a kind, and the
/// sentence a user reads is a frontend constant, so no server value reaches the copy except through
/// a named field on the view.
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
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum ConnectionSetting
{
    Address,
    ApiKey,
}

/// <summary>The result of one connection test, as the settings page reads it.</summary>
/// <remarks>
/// Discloses no API key and no response body: only a classified kind and the named values a sentence
/// needs. That keeps the route from working as a request-forwarding oracle. A caller who aims it at
/// an internal address learns which kind applied, never what answered.
/// <para>
/// <c>Generation</c> and <c>Capabilities</c> are null unless the attempt connected. A capability
/// absent from the list is a generation gap: that generation has no such thing on any build of it.
/// <c>Corroborated</c> reports the other finding, a build disagreeing with itself, and is false for
/// a build gap rather than a generation gap; it is null when there was no generation to corroborate.
/// </para>
/// <para>
/// <c>Version</c> is the instance's own version string, character for character as it sent it, and
/// is present on a success and on a refusal that names the version found. <c>OtherApplication</c>
/// is the <c>appName</c> received when it was not this product's, so the refusal names what actually
/// answered. <c>Address</c> has any credentials in it removed. <c>MissingSetting</c> is null on any
/// kind other than <see cref="ConnectionFailureKind.NotConfigured"/>, and names the address when
/// both are empty, so two runs of the same refusal read the same.
/// </para>
/// </remarks>
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
