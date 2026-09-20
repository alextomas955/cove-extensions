using System.Text.Json.Nodes;
using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

// The acting surface is split by entity kind and by verb, so a capability a generation cannot
// honour is a role it holds no registration for rather than a check inside one wide role.

/// <summary>Monitors a studio on the connected instance.</summary>
/// <remarks>
/// No member takes a caller-supplied route or verb, and the foreign id arrives already resolved from
/// a stored identity row.
/// <para>
/// Every member takes the generation, because v2 and v3 do not address a studio the same way. Which
/// routes and bodies follow from it belong to the implementation.
/// </para>
/// </remarks>
public interface IWhisparrStudioActing
{
    /// <summary>Reads the studio <paramref name="foreignId"/> names.</summary>
    /// <remarks>
    /// Returns whatever the instance answered, a not-found included: whether the entity is held at
    /// all is the caller's to classify. One generation answers that through no single route, so
    /// there the answer is assembled and reported in the same two spellings.
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
    /// Declared for a studio only: the field a future-only scope is expressed through exists on the
    /// studio resource alone.
    /// <para>
    /// The two generations then differ, and a caller has to know it: one re-applies the option over
    /// everything the instance already holds, in both directions, and the other gates only what a
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
/// Declares no scope member: the field a future-only scope is expressed through exists on the studio
/// resource alone, so a performer monitor covers the whole catalogue.
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
/// Non-acquiring: the add composes its own suppression flags, and the refresh names a catalogue
/// rather than a release. One generation has no route that adds a scene, so it holds no registration
/// for this role.
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

/// <summary>Registers a site the instance's catalogue does not hold, monitoring nothing.</summary>
/// <remarks>
/// The body is composed with the monitored flag off, no new-item rule and every acquisition
/// suppressing flag set here rather than passed in. A site's catalogue is a whole studio's worth of
/// scenes, and a monitoring add would want every one of them.
/// <para>
/// Sent once and never re-issued: a second attempt after an answer that did not arrive would act
/// twice.
/// </para>
/// <para>
/// One generation registers this role and the other holds no registration for it. On the other,
/// presence is a scene add and a site arrives as a side effect of one.
/// </para>
/// </remarks>
public interface IWhisparrSiteRegistrationActing
{
    /// <summary>Registers the site <paramref name="foreignId"/> names, monitoring nothing.</summary>
    /// <remarks>
    /// The identifier is resolved to the instance's own numeric one before the add is composed,
    /// because this generation names a site by a number of its own rather than by the identifier the
    /// library holds.
    /// </remarks>
    Task<WhisparrResponse> RegisterSiteAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct);

    /// <summary>
    /// Moves the site <paramref name="siteId"/> names to <paramref name="rootFolderPath"/>, leaving
    /// its files where they are.
    /// </summary>
    /// <remarks>
    /// No file is moved or copied: only where the instance records the site changes. The request
    /// carries no transfer parameter at all, measured against a live instance holding linked files
    /// under the old root, so adding that parameter is the edit that could move terabytes.
    /// <para>
    /// The instance relinks nothing on its own, so this member issues the catalogue re-read that
    /// links the files as part of the same call.
    /// </para>
    /// <para>
    /// Only one generation registers this role, so a target connected to the other is refused before
    /// any request leaves.
    /// </para>
    /// </remarks>
    Task<WhisparrResponse> MoveSiteRootAsync(
        Uri baseAddress,
        string apiKey,
        int siteId,
        string rootFolderPath,
        CancellationToken ct);

    /// <summary>
    /// Asks the instance to read the catalogue of the site <paramref name="siteId"/> names again.
    /// </summary>
    /// <remarks>
    /// The same re-read <see cref="MoveSiteRootAsync"/> issues, on its own, for a move whose re-read
    /// did not arrive and left the site reporting no file.
    /// <para>
    /// Nothing is written. The instance reads what is on disk under the path it already holds, so the
    /// call is repeatable and moves no file.
    /// </para>
    /// </remarks>
    Task<WhisparrResponse> RefreshSiteCatalogueAsync(
        Uri baseAddress,
        string apiKey,
        int siteId,
        CancellationToken ct);
}

/// <summary>Reads which of a set of scenes one site the instance holds has a row for.</summary>
/// <remarks>
/// A read role. Only one generation registers it, so the member takes no generation: the other names
/// a scene by its own identifier and needs no site to find it.
/// <para>
/// The site's numeric id arrives already resolved off the answer the registering pass read, so
/// nothing here can be aimed by an id a browser supplied.
/// </para>
/// </remarks>
public interface IWhisparrSiteSceneReading
{
    /// <summary>
    /// Which of <paramref name="sceneNumbers"/> the site <paramref name="siteId"/> names holds a row
    /// for, and the row's own identifier.
    /// </summary>
    /// <remarks>
    /// What the answer holds is bounded by the caller's own set, whatever the site's catalogue holds.
    /// The instance narrows its list by no parameter, so the whole answer is read as it arrives and
    /// each row is reduced to this question and dropped. There is no row cap: a cap would stop part
    /// way and report the rest as rows the instance holds none of, with nothing saying so.
    /// <para>
    /// An empty input answers an empty map with no request.
    /// </para>
    /// <para>
    /// A site whose list exceeds the transport's response bound fails this read rather than being
    /// truncated, so a caller counts that site's scenes as unresolved.
    /// </para>
    /// </remarks>
    /// <exception cref="HttpRequestException">
    /// No answer arrived, or the answer could not be read as the site's own rows. Raised rather than
    /// answered as an empty map, because an empty map would report every scene it asked about as one
    /// the instance holds no row for, which is the opposite of the truth.
    /// </exception>
    Task<IReadOnlyDictionary<int, int>> ReduceSiteSceneRowsAsync(
        Uri baseAddress,
        string apiKey,
        int siteId,
        IReadOnlyCollection<int> sceneNumbers,
        CancellationToken ct);
}

/// <summary>Reads which of a set of sites an instance holds.</summary>
/// <remarks>
/// A read role. Only one generation registers it: the other answers presence for a site through a
/// route naming the site. The numbers arrive already resolved from the metadata source, so nothing
/// here can be aimed by an identifier a browser supplied.
/// </remarks>
public interface IWhisparrHeldSiteReading
{
    /// <summary>Which of <paramref name="siteNumbers"/> the instance holds a row for.</summary>
    /// <remarks>
    /// The answer is the subset of the numbers asked about, so it is bounded by the caller's own set.
    /// One request answers a whole batch: the instance narrows its list by no parameter, so the whole
    /// answer is read as it arrives and each row is reduced to this question and dropped.
    /// <para>
    /// An empty input answers an empty set with no request. There is no row cap: a cap would stop
    /// part way and report the rest as sites the instance holds none of, with nothing saying so.
    /// </para>
    /// </remarks>
    /// <exception cref="HttpRequestException">
    /// No answer arrived, or the answer could not be read as the instance's own rows. Raised rather
    /// than answered as an empty set, because a caller comparing its library against this would
    /// otherwise report every site it asked about as one the instance does not hold.
    /// </exception>
    Task<IReadOnlySet<int>> ReduceHeldSitesAsync(
        Uri baseAddress,
        string apiKey,
        IReadOnlyCollection<int> siteNumbers,
        CancellationToken ct);
}

/// <summary>Reads what an instance holds at a path on its own filesystem.</summary>
/// <remarks>
/// A read role. Both v2 and v3 serve the route. The directory reaches the instance as a query value
/// and can never change which route is issued.
/// <para>
/// An answer's row count grows with the directory, so a caller reads one directory at a time and
/// reduces each answer rather than accumulating it.
/// </para>
/// </remarks>
public interface IWhisparrInstanceFilesystemReading
{
    /// <summary>What the instance reports at <paramref name="directory"/>, files included.</summary>
    /// <remarks>
    /// A path the instance cannot open answers an empty listing rather than a failure, so the
    /// question has a definitive yes or no. <paramref name="directory"/> reaches the instance as the
    /// directory itself whether or not its spelling carries a trailing separator.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="directory"/> is blank.</exception>
    Task<WhisparrResponse> ReadInstanceFolderAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        string directory,
        CancellationToken ct);
}

/// <summary>Tells an instance where files the library already holds are.</summary>
/// <remarks>
/// Transfers no file data. The instance is asked to link a file into place, which costs no second
/// copy while its own hard-link setting is on. With that setting off every mode duplicates the data,
/// so a caller reads the setting first and skips with the reason stated rather than copying.
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
/// A read role. Only v3 registers it: v2 answers a not-found on every per-scene route, so a caller
/// there obtains no role. No member takes a route, a verb or a query key, so the one query spelling
/// that narrows cannot be replaced with one that does not.
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
/// A read role. Only v3 registers it: v2 keeps no scene records and so keeps no scene exclusions, so
/// a caller there obtains no role rather than an empty set that would read as nothing excluded.
/// <para>
/// The answer is the subset of the identifiers asked about, so it is bounded by the caller's own set.
/// </para>
/// </remarks>
public interface IWhisparrSceneExclusionReading
{
    /// <summary>Which of <paramref name="providerSceneIds"/> the instance's user has excluded.</summary>
    /// <remarks>
    /// One request per call. The instance narrows this list by no parameter, so the whole answer is
    /// read as it arrives and each row is reduced to this question and dropped. An answer that did
    /// not arrive, or could not be read, excludes nothing.
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
    /// The removing route addresses an exclusion by the exclusion row's own identifier, while the
    /// reduce above answers scene identifiers.
    /// <para>
    /// One request, read as it arrives, stopping at the first row naming the scene. Nothing is
    /// retained between rows, so this holds one identifier whatever the instance's list holds.
    /// </para>
    /// <para>
    /// A read that did not complete is held apart from a list naming no exclusion: one claims nothing
    /// about the instance, the other is the instance stating an absence.
    /// </para>
    /// </remarks>
    Task<SceneExclusionLookup> FindSceneExclusionAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct);
}

/// <summary>What one exclusion-list read established about one scene.</summary>
/// <remarks>
/// A false read-completed flag claims nothing about the instance. The exclusion id is the exclusion
/// row's own identifier, null where the list named no exclusion for the scene.
/// </remarks>
public sealed record SceneExclusionLookup(bool ReadCompleted, int? ExclusionId)
{
    /// <summary>No whole answer arrived, so nothing about the instance was established.</summary>
    public static SceneExclusionLookup DidNotComplete { get; } = new(false, null);

    /// <summary>The list was read and names no exclusion for the scene.</summary>
    public static SceneExclusionLookup NamesNoExclusion { get; } = new(true, null);

    public static SceneExclusionLookup At(int exclusionId) => new(true, exclusionId);
}
