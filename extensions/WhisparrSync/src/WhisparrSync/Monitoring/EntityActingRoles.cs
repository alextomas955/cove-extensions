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
/// Nothing declared here can make an instance download. The verbs that can are on the grabbing
/// roles, each of which a caller has to obtain by name.
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
/// Nothing declared here can make an instance download. The verbs that can are on the grabbing
/// roles, each of which a caller has to obtain by name.
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
/// Nothing declared here can make an instance download. The verbs that can are on the grabbing
/// roles, each of which a caller has to obtain by name.
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
/// Nothing declared here can make an instance download. The verbs that can are on the grabbing
/// roles, each of which a caller has to obtain by name.
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

/// <summary>Reads what an instance holds for one catalogue scene.</summary>
/// <remarks>
/// A read role, so nothing declared here changes an instance. Only the newer generation registers
/// it: the older one answers a not-found on every per-scene route, so a caller obtains no role and
/// states what happens instead.
/// <para>
/// Narrow in the same way the acting roles are: neither member takes a route, a verb or a query key,
/// so the one query spelling that narrows cannot be replaced by a caller with one that does not.
/// </para>
/// </remarks>
public interface IWhisparrSceneStatusReading
{
    /// <summary>Whether the instance holds the <paramref name="kind"/> entity at all.</summary>
    /// <remarks>
    /// Asked once per page. An instance holding no entry for the entity holds none for any scene
    /// under it, so an absence here settles the whole page without a request per card.
    /// </remarks>
    Task<WhisparrResponse> ReadEntityPresenceAsync(
        Uri baseAddress, string apiKey, WhisparrEntityKind kind, string foreignId, CancellationToken ct);

    /// <summary>What the instance holds for the scene <paramref name="remoteId"/> names.</summary>
    /// <remarks>
    /// Answers zero rows or exactly one. The identifier travels as a single-valued query on the one
    /// key that narrows; two other spellings this instance accepts are ignored and answer with the
    /// whole catalogue.
    /// </remarks>
    Task<WhisparrResponse> ReadSceneByRemoteIdAsync(
        Uri baseAddress, string apiKey, string remoteId, CancellationToken ct);

    /// <summary>Which of <paramref name="foreignIds"/> the instance already holds an entry for.</summary>
    /// <remarks>
    /// The answer is the subset of the identifiers that were asked about, so what it carries is
    /// bounded by the caller's own set whatever the instance holds. A caller reads its own scenes in
    /// bounded batches and asks about one batch at a time.
    /// <para>
    /// An empty input answers an empty set with no request. There is no row cap: a cap would stop
    /// part way and report the rest as absent, with nothing saying so.
    /// </para>
    /// </remarks>
    /// <exception cref="HttpRequestException">
    /// No answer arrived, or the answer could not be read as the instance's own entries. Raised
    /// rather than answered as an empty set, because a caller comparing its library against this
    /// would otherwise report every scene it asked about as one the instance does not hold.
    /// </exception>
    Task<IReadOnlySet<string>> ReduceHeldScenesAsync(
        Uri baseAddress,
        string apiKey,
        IReadOnlyCollection<string> foreignIds,
        CancellationToken ct);
}

/// <summary>Reads which of a set of scenes an instance's user has excluded.</summary>
/// <remarks>
/// A read role, so nothing declared here changes an instance. Only the newer generation registers
/// it: the older one keeps no scene records at all and so keeps no scene exclusions, and a caller
/// obtains no role rather than a member answering an empty set that would read as nothing excluded.
/// <para>
/// The answer is the subset of the identifiers that were asked about, so what it carries is bounded
/// by the caller's own set whatever the instance holds. The member takes no route, no verb and no
/// query key.
/// </para>
/// </remarks>
public interface IWhisparrSceneExclusionReading
{
    /// <summary>Which of <paramref name="providerSceneIds"/> the instance's user has excluded.</summary>
    /// <remarks>
    /// One request per call. The instance narrows this list by no parameter, so the whole answer is
    /// read as it arrives and each row is reduced to this question and dropped.
    /// <para>
    /// An answer that did not arrive, or one that could not be read, excludes nothing. There is no
    /// spelling in which this can report a scene as excluded that the instance did not name.
    /// </para>
    /// </remarks>
    Task<IReadOnlySet<string>> ReduceExclusionsAsync(
        Uri baseAddress,
        string apiKey,
        IReadOnlyCollection<string> providerSceneIds,
        CancellationToken ct);

    /// <summary>
    /// The identifier of the exclusion naming <paramref name="foreignId"/>, or that the list names
    /// none.
    /// </summary>
    /// <remarks>
    /// The removing route addresses an exclusion by the exclusion row's own identifier, and the
    /// reduce above answers scene identifiers, so a caller that has to remove one reads it here.
    /// <para>
    /// One request, read as it arrives, and it stops at the first row naming the scene. Nothing is
    /// retained between rows, so what this holds is one identifier whatever the instance's list
    /// holds.
    /// </para>
    /// <para>
    /// A read that did not complete is held apart from a list naming no exclusion. The two send a
    /// caller to different answers: one claims nothing about the instance, and the other is the
    /// instance stating an absence.
    /// </para>
    /// </remarks>
    Task<SceneExclusionLookup> FindSceneExclusionAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct);
}

/// <summary>What one exclusion-list read established about one scene.</summary>
/// <param name="ReadCompleted">
/// A whole answer arrived and was read. False claims nothing about the instance at all.
/// </param>
/// <param name="ExclusionId">
/// The exclusion row's own identifier, or null where the list named no exclusion for the scene.
/// </param>
public sealed record SceneExclusionLookup(bool ReadCompleted, int? ExclusionId)
{
    /// <summary>No whole answer arrived, so nothing about the instance was established.</summary>
    public static SceneExclusionLookup DidNotComplete { get; } = new(false, null);

    /// <summary>The list was read and names no exclusion for the scene.</summary>
    public static SceneExclusionLookup NamesNoExclusion { get; } = new(true, null);

    /// <summary>The list names the scene, under <paramref name="exclusionId"/>.</summary>
    public static SceneExclusionLookup At(int exclusionId) => new(true, exclusionId);
}
