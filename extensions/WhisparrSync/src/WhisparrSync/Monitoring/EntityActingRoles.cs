using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

// The acting surface is split by entity kind and by verb, so a capability a generation cannot
// honour is a role it holds no registration for rather than a check inside one wide role. One file
// per role would be four files declaring one interface each.

/// <summary>Monitors a studio on the connected instance.</summary>
/// <remarks>
/// Narrow in the same way the read seam is: no member takes a caller-supplied route and none takes an
/// HTTP verb. The foreign id arrives already resolved from a stored identity row, so aiming this
/// extension's credential at an arbitrary entity is impossible in the signature rather than a rule
/// each call site has to keep.
/// <para>
/// Nothing declared here can make an instance download. The one verb that can is
/// <see cref="IWhisparrSearchGrabbing.SearchMonitoredAsync"/>, on a role of its own that a caller has
/// to obtain by name.
/// </para>
/// <para>
/// Every member names the connected generation, because both generations honour this role and neither
/// addresses a studio the way the other does. It is the one thing a call site supplies that the
/// implementation could not read for itself, and it names a lineage rather than a route: which routes
/// and which bodies follow from it belong to the implementation, so no call site chooses either.
/// </para>
/// </remarks>
public interface IWhisparrStudioActing
{
    /// <summary>Reads the studio <paramref name="foreignId"/> names.</summary>
    /// <remarks>
    /// Returns whatever the instance answered, including a not-found: whether the entity is held at
    /// all is the precondition the caller classifies. One generation answers that question through no
    /// single route, so on it the answer is assembled and reported in the same two spellings.
    /// </remarks>
    Task<WhisparrResponse> ReadStudioAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        string foreignId,
        CancellationToken ct);

    /// <summary>Adds the studio <paramref name="foreignId"/> names, monitored at <paramref name="scope"/>.</summary>
    /// <remarks>
    /// Sent once and never re-issued: a second attempt after an answer that did not arrive would act
    /// twice. Every flag that suppresses acquisition is composed here rather than passed in, so no
    /// caller can leave one out.
    /// </remarks>
    Task<WhisparrResponse> AddMonitoredStudioAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        string foreignId,
        MonitorScope scope,
        AddDefaults defaults,
        CancellationToken ct);

    /// <summary>Sets only the monitored flag on the studio <paramref name="entityId"/> names.</summary>
    /// <remarks>
    /// Every other field the instance holds for that studio is left unset, and an unset field is not
    /// applied. Setting the flag false governs what a later catalogue addition does and retracts
    /// nothing already wanted.
    /// </remarks>
    Task<WhisparrResponse> SetStudioMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int entityId,
        bool monitored,
        CancellationToken ct);

    /// <summary>Sets the monitor scope on the studio <paramref name="entityId"/> names.</summary>
    /// <remarks>
    /// Declared for a studio and for no other kind, because the field a future-only scope is expressed
    /// through exists on the studio resource alone.
    /// <para>
    /// What the two generations then do differs and a caller has to know it: one re-applies the option
    /// over everything the instance already holds, in both directions, and the other gates only what a
    /// later catalogue read adds.
    /// </para>
    /// </remarks>
    Task<WhisparrResponse> SetStudioScopeAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int entityId,
        MonitorScope scope,
        CancellationToken ct);
}

/// <summary>Monitors a performer on the connected instance.</summary>
/// <remarks>
/// Declares no scope member at all. The field a future-only scope is expressed through exists on the
/// studio resource and on no other, so a performer scope member would be a promise its own signature
/// could not keep. A performer monitor covers the whole catalogue, and that consequence is stated
/// where it is chosen rather than implied by a member that cannot honour it.
/// <para>
/// Nothing declared here can make an instance download, and the one verb that can is on
/// <see cref="IWhisparrSearchGrabbing"/>, a role a caller has to obtain by name.
/// </para>
/// </remarks>
public interface IWhisparrPerformerActing
{
    /// <summary>Reads the performer <paramref name="foreignId"/> names.</summary>
    /// <inheritdoc cref="IWhisparrStudioActing.ReadStudioAsync" path="/remarks"/>
    Task<WhisparrResponse> ReadPerformerAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct);

    /// <summary>Adds the performer <paramref name="foreignId"/> names, monitored.</summary>
    /// <inheritdoc cref="IWhisparrStudioActing.AddMonitoredStudioAsync" path="/remarks"/>
    Task<WhisparrResponse> AddMonitoredPerformerAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct);

    /// <summary>Sets only the monitored flag on the performer <paramref name="entityId"/> names.</summary>
    /// <inheritdoc cref="IWhisparrStudioActing.SetStudioMonitoredAsync" path="/remarks"/>
    Task<WhisparrResponse> SetPerformerMonitoredAsync(
        Uri baseAddress, string apiKey, int entityId, bool monitored, CancellationToken ct);
}

/// <summary>Registers scenes an instance's catalogue does not hold.</summary>
/// <remarks>
/// Non-acquiring by construction: the add composes its own suppression flags, and the refresh names a
/// catalogue rather than a release. One generation has no route that adds a scene at all, so it holds
/// no registration for this role rather than a member that refuses once it is called.
/// <para>
/// Nothing declared here can make an instance download, and the one verb that can is on
/// <see cref="IWhisparrSearchGrabbing"/>, a role a caller has to obtain by name.
/// </para>
/// </remarks>
public interface IWhisparrMissingSceneActing
{
    /// <summary>Adds the scene <paramref name="foreignId"/> names to the instance's catalogue.</summary>
    /// <inheritdoc cref="IWhisparrStudioActing.AddMonitoredStudioAsync" path="/remarks"/>
    Task<WhisparrResponse> AddSceneAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct);

    /// <summary>Asks the instance to re-read the catalogue of the entity <paramref name="entityId"/> names.</summary>
    /// <remarks>
    /// Pulls whatever the instance's own metadata provider lists for that entity, which is the only
    /// way a catalogue arrives on one of the two generations. Sent once, like every acting request.
    /// </remarks>
    Task<WhisparrResponse> RefreshCatalogueAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        int entityId,
        CancellationToken ct);
}

/// <summary>Tells an instance where files the library already holds are.</summary>
/// <remarks>
/// Transfers no file data. The instance is asked to link a file into place, which costs no second
/// copy while its own hard-link setting is on. With that setting off there is no mode to ask for that
/// would not duplicate the data, so a caller reads the setting first and skips with the reason stated
/// rather than copying.
/// <para>
/// Nothing declared here can make an instance download, and the one verb that can is on
/// <see cref="IWhisparrSearchGrabbing"/>, a role a caller has to obtain by name.
/// </para>
/// </remarks>
public interface IWhisparrReflectOwnedActing
{
    /// <summary>Reads whether the instance links a file into place rather than copying it.</summary>
    Task<WhisparrResponse> ReadHardlinkSettingAsync(
        Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>Parses <paramref name="folder"/> into one row per file the instance could take.</summary>
    /// <remarks>
    /// The row count grows with the folder, so a caller reads one folder at a time and hands its rows
    /// straight into one request rather than accumulating them. <paramref name="folder"/> is a
    /// filesystem directory and never a route segment: it reaches the instance as a query value and
    /// cannot change which route is issued.
    /// </remarks>
    Task<WhisparrResponse> ListImportableFilesAsync(
        Uri baseAddress, string apiKey, string folder, CancellationToken ct);

    /// <summary>Attaches the files <paramref name="files"/> describes to what the instance holds.</summary>
    /// <remarks>
    /// Takes the rows a parse answered rather than composed ones: the quality and the languages a row
    /// carries cannot be fabricated, and the instance refuses a row missing either. Sent once, like
    /// every acting request.
    /// </remarks>
    Task<WhisparrResponse> AttachOwnedFilesAsync(
        Uri baseAddress, string apiKey, JsonNode files, CancellationToken ct);
}
