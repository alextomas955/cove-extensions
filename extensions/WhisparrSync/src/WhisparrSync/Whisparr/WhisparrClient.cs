using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Scene;
using V2Api = Whisparr2.Net.Api;
using V2Client = Whisparr2.Net.Client;
using V2Model = Whisparr2.Net.Model;
using V3Api = Whisparr3.Net.Api;
using V3Client = Whisparr3.Net.Client;
using V3Model = Whisparr3.Net.Model;

namespace WhisparrSync.Whisparr;

/// <summary>The class of work a request does, which is what its retry behaviour is keyed on.</summary>
/// <remarks>
/// A class that must never be re-issued is a member with no entry in
/// <see cref="WhisparrRetryPolicy"/>'s table, which is a data change rather than a structural one.
/// </remarks>
public enum WhisparrVerbClass
{
    /// <summary>A request that only reads. Re-issuing one creates nothing and grabs nothing.</summary>
    Read,

    /// <summary>
    /// Changes the instance's own configuration. Never re-issued: a second attempt after an answer
    /// that did not arrive would act twice.
    /// </summary>
    Configure,

    /// <summary>
    /// Changes what an instance monitors. Never re-issued: a second attempt after an answer that did
    /// not arrive would act twice.
    /// </summary>
    Act,

    /// <summary>
    /// The one class that can make an instance download. Never re-issued, and reachable only through
    /// the role a caller obtains by name.
    /// </summary>
    Grab,
}

/// <summary>How many attempts a verb class is allowed.</summary>
/// <remarks>
/// Per verb class rather than uniform, because a uniform retry is what would silently re-issue a
/// request that acts. An unlisted class gets <see cref="NoRetry"/>, so the safe answer is the
/// default and a retrying class has to be written down.
/// </remarks>
public static class WhisparrRetryPolicy
{
    /// <summary>One attempt: the request is issued once and a failure is reported.</summary>
    public const int NoRetry = 1;

    private static readonly Dictionary<WhisparrVerbClass, int> AttemptsByVerbClass = new()
    {
        [WhisparrVerbClass.Read] = 2,
    };

    /// <summary>How many attempts <paramref name="verbClass"/> is allowed.</summary>
    public static int AttemptsFor(WhisparrVerbClass verbClass)
        => AttemptsByVerbClass.GetValueOrDefault(verbClass, NoRetry);
}

/// <summary>What one Whisparr request answered with.</summary>
/// <remarks>
/// The content type is the header as received, unparsed. A rejected key answers with none on both
/// generations, so an empty one is a real observation. The body is empty when there was none.
/// </remarks>
public sealed record WhisparrResponse(int StatusCode, string? ContentType, string Body)
{
    /// <summary>Why no entity was named, where a seam established that without an instance.</summary>
    /// <remarks>
    /// A v2 site is addressed by a number the metadata source issues, so a site nothing could be
    /// numbered for is refused before any request leaves and there is no status to classify. The
    /// reason is stated here instead of inventing a status.
    /// <para>
    /// <see cref="MonitorRefusalKind.None"/> on every answer that came from an instance, which is
    /// classified from its status.
    /// </para>
    /// </remarks>
    public MonitorRefusalKind Refusal { get; init; } = MonitorRefusalKind.None;
}

/// <summary>
/// The one seam through which this extension talks to a Whisparr instance.
/// </summary>
/// <remarks>
/// Narrow by design: no method takes a caller-supplied path and none takes an HTTP verb, so no call
/// site can express a request that makes Whisparr search for or download anything.
/// <para>
/// The verbs that change what an instance monitors are not here. They are the roles in
/// <c>WhisparrSync.Monitoring</c>, and the one verb that can make an instance download is alone on
/// <c>IWhisparrSearchGrabbing</c> there.
/// </para>
/// </remarks>
public interface IWhisparrClient
{
    /// <summary>Reads the status document from the instance at <paramref name="baseAddress"/>.</summary>
    /// <remarks>
    /// Returns whatever the instance answered, including a non-success status: classifying the answer
    /// belongs to the caller. Throws only when no answer arrived at all.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="baseAddress"/> is relative, or its scheme is neither http nor https.
    /// </exception>
    /// <exception cref="HttpRequestException">The request produced no response.</exception>
    /// <exception cref="IOException">
    /// The response ended before the length it declared. The body is read out of the response stream
    /// rather than buffered inside the send, so a dropped connection raises this rather than
    /// <see cref="HttpRequestException"/>. Both mean no whole answer arrived, so a caller containing
    /// one contains the other.
    /// </exception>
    /// <exception cref="TaskCanceledException">The request outlived the client's timeout.</exception>
    Task<WhisparrResponse> ReadStatusAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>Reads the notification schema, which declares what a connection can be told.</summary>
    Task<WhisparrResponse> ReadNotificationSchemaAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>Reads every notification the instance holds.</summary>
    Task<WhisparrResponse> ListNotificationsAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>Reads the library roots the instance reports for itself.</summary>
    /// <remarks>
    /// The instance's own root folders are not carried on the import event it sends, so a consumer
    /// resolving a reported file path against its root has no other source for them.
    /// </remarks>
    Task<WhisparrResponse> ReadRootFoldersAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>Reads the quality profiles the instance offers.</summary>
    /// <remarks>
    /// An add cannot be composed without one, and which profiles exist is the instance's own: a
    /// profile id it does not offer is refused by one generation and accepted by the other.
    /// </remarks>
    Task<WhisparrResponse> ReadQualityProfilesAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>
    /// Reads one page of the instance's import history, with each record's own metadata entity.
    /// </summary>
    /// <remarks>
    /// The newest-first order is asked for and not relied on: whether the route honours the request
    /// is unmeasured, so a caller reads the page's own order and refuses one it cannot walk. Pages
    /// count from one. The generation decides which metadata entity is embedded; the route and the
    /// order belong to the seam.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="page"/> or <paramref name="pageSize"/> is below one, or
    /// <paramref name="generation"/> is not a generation this reads.
    /// </exception>
    Task<WhisparrResponse> ReadHistoryAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>Reads the command <paramref name="commandId"/> names back off the instance.</summary>
    /// <remarks>
    /// Establishes that the instance holds the command and nothing about anything being downloaded.
    /// The identifier comes off the answer to the post rather than from a caller.
    /// </remarks>
    Task<WhisparrResponse> ReadCommandAsync(
        Uri baseAddress, string apiKey, int commandId, CancellationToken ct);

    /// <summary>Creates one notification.</summary>
    /// <remarks>
    /// Never re-issued on a failure. The instance refuses a duplicate name, but that refusal cannot
    /// be told from a real one, so no second attempt is made.
    /// </remarks>
    Task<WhisparrResponse> CreateNotificationAsync(
        Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct);

    /// <summary>Replaces the notification with <paramref name="id"/>.</summary>
    /// <inheritdoc cref="CreateNotificationAsync" path="/remarks"/>
    Task<WhisparrResponse> UpdateNotificationAsync(
        Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct);
}

// The acting roles are implemented here rather than on a type of their own: this is the one type
// holding an outbound surface, and every route invariant reflects over it.
//
// Each generation's requests are composed by its own generated client, v3 through Whisparr3Gateway
// and v2 through Whisparr2Gateway. The routes still declared here are sent through the held
// HttpClient.
internal sealed class WhisparrClient(
    HttpClient http,
    Whisparr3Gateway v3Gateway,
    Whisparr2Gateway v2Gateway,
    ISiteNumberPort siteNumbers,
    ILogger log)
    : IWhisparrClient,
        IWhisparrStudioActing,
        IWhisparrPerformerActing,
        IWhisparrMissingSceneActing,
        IWhisparrSiteRegistrationActing,
        IWhisparrReflectOwnedActing,
        IWhisparrSearchGrabbing,
        IWhisparrSceneSearchGrabbing,
        IWhisparrSceneStatusReading,
        IWhisparrSceneExclusionReading,
        IWhisparrSceneMonitorActing,
        IWhisparrSceneExclusionActing,
        IWhisparrSiteSceneReading,
        IWhisparrHeldSiteReading,
        IWhisparrInstanceFilesystemReading
{
    // The header both v2 and v3 authenticate an API request with.
    internal const string ApiKeyHeader = "X-Api-Key";

    // Relative, so they compose onto a base address carrying a URL base (a reverse-proxy subpath).
    // Both generations serve the v3 route family; the version in the path is not the generation.
    private const string NotificationPath = "api/v3/notification";

    // Every self-composed route is declared on this type, whichever role issues it: the route
    // invariant reads this type's own literals, so a constant declared elsewhere is invisible to it.
    internal const string StudioPath = "api/v3/studio";
    internal const string ExclusionsPath = "api/v3/exclusions";

    // The one status composed rather than received. Whisparr v2 answers "do you hold this site" only
    // as a row inside its own list, so an absent row is reported in the spelling a caller already
    // classifies.
    private const int AssembledNotHeld = 404;

    // No request was sent, so there is no status to report. Zero is no status rather than a composed
    // one: every caller of an answer carrying it reads the refusal, which outranks the status.
    private const int NoInstanceStatus = 0;

    // The member naming the verb on a composed command body.
    private const string CommandNameProperty = "name";

    // Newest-first is the only order a walk that stops at a stored position can read, so the order
    // belongs to the verb rather than to a call.
    private const string NewestFirstSortKey = "date";

    // How long one attempt may take before it is reported as unreachable.
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    // A read of everything an instance holds is answered only once the instance has built all of it,
    // so its cost grows with the holdings rather than signalling that it cannot be reached: a v2
    // instance holding 512 sites takes about 20 seconds to answer GET /api/v3/series, all of it
    // before the first byte. Set well above that, and below where a waiting reader reads it as hung.
    internal static readonly TimeSpan LibraryReadTimeout = TimeSpan.FromSeconds(120);

    // A login redirect is a real deployment; an unbounded chain of them is not.
    internal const int MaxRedirects = 3;

    // How much of one answer is held in memory before it is refused. Exceeding it answers
    // MonitorRefusalKind.AnswerTooLargeToRead with an empty body, never a short body that would
    // parse as a valid page.
    internal const long MaxResponseBytes = 8L * 1024 * 1024;

    // One byte past the bound tells an answer at the bound from one over it.
    private const long ReadCeilingBytes = MaxResponseBytes + 1;

    private const int ReadChunkBytes = 64 * 1024;

    private static readonly JsonSerializerOptions ExclusionRowShape = new(JsonSerializerDefaults.Web);

    // The two members of an exclusion row that are read. Declared with no others so a row costs one
    // small object that is dropped before the next is read. The id is the row's own, which the
    // removing route addresses; the foreign id is the scene's.
    private sealed record ExclusionRow(int Id, string? ForeignId);

    private static readonly JsonSerializerOptions HeldSceneRowShape = new(JsonSerializerDefaults.Web);

    // The two members of an answered entry that are read. Declared with no others so a chunk's
    // answer costs one small object per hit rather than the whole resource the instance sent.
    private sealed record HeldSceneRow(string? StashId, string? ForeignId);

    public async Task<WhisparrResponse> ReadStatusAsync(
        Uri baseAddress,
        string apiKey,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (!IsAddressable(baseAddress))
        {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A Whisparr address must be an absolute http or https URL; the scheme given was '{baseAddress.Scheme}'."),
                nameof(baseAddress));
        }

        return await GeneratedReadAsync(
            baseAddress, apiKey, api => api.Api<V3Api.ISystemApi>().GetSystemStatusAsync(ct))
            .ConfigureAwait(false);
    }

    public Task<WhisparrResponse> ReadNotificationSchemaAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress, apiKey, api => api.Api<V3Api.INotificationApi>().ListNotificationSchemaAsync(ct));

    public Task<WhisparrResponse> ListNotificationsAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress, apiKey, api => api.Api<V3Api.INotificationApi>().ListNotificationAsync(ct));

    public Task<WhisparrResponse> ReadRootFoldersAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress, apiKey, api => api.Api<V3Api.IRootFolderApi>().ListRootFolderAsync(ct));

    public Task<WhisparrResponse> ReadQualityProfilesAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress, apiKey, api => api.Api<V3Api.IQualityProfileApi>().ListQualityProfileAsync(ct));

    public Task<WhisparrResponse> ReadHistoryAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);

        return generation switch
        {
            WhisparrGeneration.V3 => GeneratedReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IHistoryApi>().GetHistoryAsync(
                    page: page,
                    pageSize: pageSize,
                    sortKey: NewestFirstSortKey,
                    sortDirection: V3Model.SortDirection.Descending,
                    includeMovie: true,
                    cancellationToken: ct)),

            // v2 and v3 name their own metadata entity on this route, and that entity carries the
            // identifier the two ingest channels agree on. Embedded on the same request, so a page
            // costs one request whatever it holds.
            WhisparrGeneration.V2 => GeneratedV2ReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V2Api.IHistoryApi>().GetHistoryAsync(
                    page: page,
                    pageSize: pageSize,
                    sortKey: NewestFirstSortKey,
                    sortDirection: V2Model.SortDirection.Descending,
                    includeEpisode: true,
                    cancellationToken: ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };
    }

    public Task<WhisparrResponse> ReadCommandAsync(
        Uri baseAddress, string apiKey, int commandId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(commandId, 1);

        return GeneratedReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.ICommandApi>().GetCommandByIdAsync(commandId, ct));
    }

    public Task<WhisparrResponse> CreateNotificationAsync(
        Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct)
        => ConfigureAsync(baseAddress, apiKey, HttpMethod.Post, NotificationPath, body, ct);

    public Task<WhisparrResponse> UpdateNotificationAsync(
        Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct)
        => ConfigureAsync(
            baseAddress,
            apiKey,
            HttpMethod.Put,
            string.Create(CultureInfo.InvariantCulture, $"{NotificationPath}/{id}"),
            body,
            ct);

    public Task<WhisparrResponse> ReadStudioAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        string foreignId,
        CancellationToken ct)
        => generation switch
        {
            WhisparrGeneration.V3 => GeneratedReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IStudioApi>().GetStudioByStudioForeignIdAsync(Named(foreignId), ct)),
            WhisparrGeneration.V2 => ReadHeldSeriesAsync(baseAddress, apiKey, foreignId, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    public Task<WhisparrResponse> AddMonitoredStudioAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        string foreignId,
        MonitorScope scope,
        AddDefaults defaults,
        CancellationToken ct)
        => generation switch
        {
            WhisparrGeneration.V3 => GeneratedActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IStudioApi>().CreateStudioAsync(
                    V3BodyProjector.AddStudio(foreignId, scope, defaults, DateTimeOffset.UtcNow), ct)),
            WhisparrGeneration.V2 => AddMonitoredSeriesAsync(
                baseAddress, apiKey, foreignId, scope, defaults, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    public Task<WhisparrResponse> SetStudioMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int entityId,
        bool monitored,
        CancellationToken ct)
        => generation switch
        {
            WhisparrGeneration.V3 => GeneratedActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IStudioEditorApi>().PutStudioEditorAsync(
                    V3BodyProjector.SetStudioMonitored(entityId, monitored), ct)),
            WhisparrGeneration.V2 => GeneratedV2ActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V2Api.ISeriesEditorApi>().PutSeriesEditorAsync(
                    V2BodyProjector.SetMonitored(entityId, monitored), ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    // On v3 a field-scoped patch carrying only what changes: a whole-resource replace would write
    // back a resource read a moment earlier, dropping whatever the read did not answer with. On v2
    // the flag travels on a list of exactly one row id, the only shape that route takes.
    public Task<WhisparrResponse> SetSceneMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int sceneId,
        bool monitored,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sceneId, 1);

        return generation switch
        {
            WhisparrGeneration.V3 => GeneratedActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IMovieApi>().PatchMovieByIdAsync(
                    sceneId, V3BodyProjector.SceneMonitorPatch(monitored), ct)),
            WhisparrGeneration.V2 => GeneratedV2ActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V2Api.IEpisodeApi>().PutEpisodeMonitorAsync(
                    episodesMonitoredResource: V2BodyProjector.MonitorScene(sceneId, monitored),
                    cancellationToken: ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };
    }

    // One request against the site's own row list. The members that would attach images, files or
    // the site resource are left off, so nothing arrives that this read drops.
    public async Task<IReadOnlyDictionary<int, int>> ReduceSiteSceneRowsAsync(
        Uri baseAddress,
        string apiKey,
        int siteId,
        IReadOnlyCollection<int> sceneNumbers,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(siteId, 1);
        ArgumentNullException.ThrowIfNull(sceneNumbers);

        if (sceneNumbers.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var listed = await GeneratedV2ReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.IEpisodeApi>().ListEpisodeAsync(
                seriesId: siteId, cancellationToken: ct)).ConfigureAwait(false);

        // Raised rather than answered as an empty map. An empty map would report every scene it
        // asked about as one this site holds no row for, which is the opposite of the truth.
        if (Refused(listed))
        {
            throw new HttpRequestException(
                "The site's own scene rows could not be read, so which of them the instance holds "
                    + "was not established.");
        }

        return V2ListProjector.RowsByNumber(listed.Body, sceneNumbers)
            ?? throw new HttpRequestException(
                "The answer to the site's own scene rows is not a list of rows at all.");
    }

    // One request against the instance's own site list, whatever the batch holds. Narrowing would
    // not help: v2 builds the whole set before filtering, so ?tvdbId= answers a single row no faster
    // than the unfiltered list answers all of them. The read is bounded by LibraryReadTimeout,
    // because what it waits on is the instance's own work over its holdings.
    public async Task<IReadOnlySet<int>> ReduceHeldSitesAsync(
        Uri baseAddress,
        string apiKey,
        IReadOnlyCollection<int> siteNumbers,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(siteNumbers);

        if (siteNumbers.Count == 0)
        {
            return new HashSet<int>();
        }

        var listed = await GeneratedV2ReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.ISeriesApi>().ListSeriesAsync(cancellationToken: ct),
            LibraryReadTimeout)
            .ConfigureAwait(false);

        // Raised rather than answered as an empty set. An empty set would report every site it asked
        // about as one the instance holds none of, and a caller acting on that registers the whole
        // library a second time.
        if (Refused(listed))
        {
            throw new HttpRequestException(
                "The instance's own site list could not be read, so which of the sites it holds was "
                    + "not established.");
        }

        var rows = V2ListProjector.RowsByNumber(listed.Body, siteNumbers)
            ?? throw new HttpRequestException(
                "The answer to the instance's own site list is not a list of rows at all.");

        return rows.Keys.ToHashSet();
    }

    public Task<WhisparrResponse> AddSceneExclusionAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
        => GeneratedActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IImportListExclusionApi>().CreateExclusionsAsync(
                V3BodyProjector.SceneExclusion(Named(foreignId)), ct));

    public Task<WhisparrResponse> RemoveSceneExclusionAsync(
        Uri baseAddress, string apiKey, int exclusionId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(exclusionId, 1);

        return GeneratedActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IImportListExclusionApi>().DeleteExclusionsAsync(exclusionId, ct));
    }

    public Task<WhisparrResponse> SetStudioScopeAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int entityId,
        MonitorScope scope,
        CancellationToken ct)
        => generation switch
        {
            WhisparrGeneration.V3 => SetStudioDateGateAsync(baseAddress, apiKey, entityId, scope, ct),

            // Re-applied over the existing catalogue in one request, so nothing is read first. The
            // route answers an empty body with a server failure, so the body is what makes it work.
            WhisparrGeneration.V2 => GeneratedV2ActAsync(
                baseAddress,
                apiKey,
                api => api.Api<V2Api.ISeasonPassApi>().CreateSeasonPassAsync(
                    V2BodyProjector.SetScope(entityId, scope), ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    private async Task<WhisparrResponse> SetStudioDateGateAsync(
        Uri baseAddress, string apiKey, int entityId, MonitorScope scope, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);

        // Read then replaced, because the editor resource declares no add-time date gate: a scope
        // sent there is accepted and applies nothing. The read is idempotent and the replace is sent
        // once.
        //
        // Composed here rather than by the generated client, which carries a fixed member set: the
        // replacement is the answer itself with two members changed, and a member the generated
        // resource does not declare would be dropped on the way back out.
        var path = string.Create(CultureInfo.InvariantCulture, $"{StudioPath}/{entityId}");
        var held = await ReadAsync(baseAddress, apiKey, path, ct).ConfigureAwait(false);
        if (MonitoringProjector.AsObject(held.Body) is not { } studio)
        {
            return held;
        }

        return await ActAsync(
            baseAddress, apiKey, HttpMethod.Put, path,
            V3BodyProjector.WithScope(studio, scope, DateTimeOffset.UtcNow), ct)
            .ConfigureAwait(false);
    }

    // Whether v2's instance holds the entity an identifier names. One read, narrowed to the site's
    // number, and only the matched entry is carried onward.
    //
    // How long it takes varies with the holdings, so the read is bounded by LibraryReadTimeout. v2
    // builds its whole set before filtering, so the narrowed answer is no faster than the unfiltered
    // one: an instance holding 512 sites answers in about 41 seconds. Bounded as a per-item call it
    // times out on every site, and the site pass then registers and moves nothing.
    private async Task<WhisparrResponse> ReadHeldSeriesAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
    {
        var numbered = await siteNumbers.ResolveSiteNumberAsync(baseAddress, apiKey, foreignId, ct)
            .ConfigureAwait(false);
        if (numbered.Number is not { } siteNumber)
        {
            return NoSiteNumber(numbered);
        }

        var listed = await GeneratedV2ReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.ISeriesApi>().ListSeriesAsync(
                tvdbId: siteNumber, cancellationToken: ct),
            LibraryReadTimeout).ConfigureAwait(false);
        if (Refused(listed))
        {
            return listed;
        }

        return V2ListProjector.HeldEntry(listed.Body, siteNumber) is { } held
            ? new WhisparrResponse(listed.StatusCode, listed.ContentType, held.ToJsonString())
            : new WhisparrResponse(AssembledNotHeld, listed.ContentType, string.Empty);
    }

    // The add carries the number and the scope and nothing the metadata source said: the instance
    // resolves the site's own title and slug from that number.
    private async Task<WhisparrResponse> AddMonitoredSeriesAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        MonitorScope scope,
        AddDefaults defaults,
        CancellationToken ct)
    {
        var numbered = await siteNumbers.ResolveSiteNumberAsync(baseAddress, apiKey, foreignId, ct)
            .ConfigureAwait(false);
        if (numbered.Number is not { } siteNumber)
        {
            return NoSiteNumber(numbered);
        }

        return await GeneratedV2ActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.ISeriesApi>().CreateSeriesAsync(
                V2BodyProjector.AddStudio(siteNumber, scope, defaults),
                ct)).ConfigureAwait(false);
    }

    // The same request the monitoring add sends, with the presence-only body, so the catalogue the
    // instance then reads for the site is wanted by nothing.
    public async Task<WhisparrResponse> RegisterSiteAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
    {
        var numbered = await siteNumbers.ResolveSiteNumberAsync(baseAddress, apiKey, foreignId, ct)
            .ConfigureAwait(false);
        if (numbered.Number is not { } siteNumber)
        {
            return NoSiteNumber(numbered);
        }

        return await GeneratedV2ActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.ISeriesApi>().CreateSeriesAsync(
                V2BodyProjector.RegisterSite(siteNumber, defaults),
                ct)).ConfigureAwait(false);
    }

    // One read and one update, then the catalogue re-read that links the files. The update's body is
    // the resource the read answered rather than one composed here. No transfer parameter is named,
    // which is what leaves the files where they are.
    //
    // The re-read is not optional: the update alone rewrites where the instance records the site and
    // links nothing, so the site reports no file until the catalogue is re-read.
    public async Task<WhisparrResponse> MoveSiteRootAsync(
        Uri baseAddress,
        string apiKey,
        int siteId,
        string rootFolderPath,
        CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(siteId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolderPath);

        var (read, resource) = await ReadSeriesResourceAsync(baseAddress, apiKey, siteId, ct)
            .ConfigureAwait(false);
        if (resource is null)
        {
            // A success status carrying nothing the model could be read from arrives here too, and
            // its own status classifies as accepted. Returning it unchanged would report a move the
            // caller counts as done while no update was sent and the site still sits where it was.
            return Refused(read)
                ? read
                : read with { Refusal = MonitorRefusalKind.InstanceRefused };
        }

        var moved = await GeneratedV2ActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.ISeriesApi>().UpdateSeriesAsync(
                siteId.ToString(CultureInfo.InvariantCulture),
                seriesResource: V2BodyProjector.MovedSiteRoot(resource, rootFolderPath),
                cancellationToken: ct)).ConfigureAwait(false);
        if (Refused(moved))
        {
            return moved;
        }

        var linked = await RefreshSiteCatalogueAsync(baseAddress, apiKey, siteId, ct)
            .ConfigureAwait(false);

        return Refused(linked) ? linked : moved;
    }

    public Task<WhisparrResponse> RefreshSiteCatalogueAsync(
        Uri baseAddress, string apiKey, int siteId, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(siteId, 1);

        var (verb, payload) = VerbAndPayload(V2BodyProjector.RefreshCatalogue(siteId));
        return GeneratedV2ActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.CommandApi>().SendCommandAsync(verb, payload, ct));
    }

    // The typed resource beside the answer, because the update re-sends what the read answered.
    // Re-parsing the text answer into a member set named here would drop every member not named
    // here: the tags, the per-year flags, and whatever a later instance build adds.
    private async Task<(WhisparrResponse Answer, V2Model.SeriesResource? Held)>
        ReadSeriesResourceAsync(Uri baseAddress, string apiKey, int siteId, CancellationToken ct)
    {
        V2Model.SeriesResource? held = null;
        var answered = await GeneratedV2ReadAsync(
            baseAddress,
            apiKey,
            async api =>
            {
                var read = await api.Api<V2Api.ISeriesApi>()
                    .GetSeriesByIdAsync(siteId, cancellationToken: ct).ConfigureAwait(false);
                read.TryOk(out held);
                return read;
            }).ConfigureAwait(false);

        return Refused(answered) ? (answered, null) : (answered, held);
    }

    // Nothing was sent, so there is no status and the refusal is the whole of what a caller reads. A
    // source naming no site is the no-identity reading; a source that was not reached is not, and
    // reporting it as unidentified would send a reader to fix an identity that may be correct.
    private static WhisparrResponse NoSiteNumber(WhisparrSiteNumber numbered)
        => new(NoInstanceStatus, null, string.Empty)
        {
            Refusal = numbered.WasReached
                ? MonitorRefusalKind.NoIdentityInThisNamespace
                : MonitorRefusalKind.InstanceRefused,
        };

    private static bool IsSuccess(int statusCode) => statusCode is >= 200 and < 300;

    // A refusal the send read for itself outranks the status, which is the rule
    // MonitoringProjector.Classify applies too. An answer past the read bound arrives with whatever
    // status the instance gave, so a success one, and with an empty body: parsing that body would
    // report the entity as absent and lose the reason the send established.
    private static bool Refused(WhisparrResponse answered)
        => answered.Refusal is not MonitorRefusalKind.None || !IsSuccess(answered.StatusCode);

    public Task<WhisparrResponse> ReadPerformerAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IPerformerApi>()
                .GetPerformerByPerformerForeignIdAsync(Named(foreignId), ct));

    public Task<WhisparrResponse> AddMonitoredPerformerAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => GeneratedActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IPerformerApi>().CreatePerformerAsync(
                V3BodyProjector.AddPerformer(foreignId, defaults), ct));

    public Task<WhisparrResponse> SetPerformerMonitoredAsync(
        Uri baseAddress, string apiKey, int entityId, bool monitored, CancellationToken ct)
        => GeneratedActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IPerformerEditorApi>().PutPerformerEditorAsync(
                V3BodyProjector.SetPerformerMonitored(entityId, monitored), ct));

    public Task<WhisparrResponse> AddSceneAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => GeneratedActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IMovieApi>().CreateMovieAsync(
                V3BodyProjector.AddScene(foreignId, defaults), ct));

    public Task<WhisparrResponse> RefreshCatalogueAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        int entityId,
        CancellationToken ct)
    {
        var command = V3BodyProjector.RefreshCatalogue(kind, entityId);
        return GeneratedCommandAsync(baseAddress, apiKey, command, ct);
    }

    // The operation is chosen here from the entity kind, so no caller can aim the stored credential
    // at a route of its own naming.
    public Task<WhisparrResponse> ReadEntityPresenceAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        string foreignId,
        CancellationToken ct)
        => kind switch
        {
            WhisparrEntityKind.Studio => GeneratedReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IStudioApi>()
                    .GetStudioByStudioForeignIdAsync(Named(foreignId), ct)),
            WhisparrEntityKind.Performer => GeneratedReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IPerformerApi>()
                    .GetPerformerByPerformerForeignIdAsync(Named(foreignId), ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    // One key, single-valued. Repeating it answers only the first value's row, comma-joining
    // answers nothing, and the two plural spellings v3 accepts are ignored and answer with the whole
    // catalogue.
    public Task<WhisparrResponse> ReadSceneByRemoteIdAsync(
        Uri baseAddress, string apiKey, string remoteId, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IMovieApi>().ListMovieAsync(
                stashId: Named(remoteId), cancellationToken: ct));

    // Each row is reduced to one question, so what this holds is the caller's own set and never the
    // instance's. There is no row cap: a cap would stop part way and report the rest as not
    // excluded, with nothing saying so.
    public async Task<IReadOnlySet<string>> ReduceExclusionsAsync(
        Uri baseAddress,
        string apiKey,
        IReadOnlyCollection<string> providerSceneIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providerSceneIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // Keyed without regard to case: an identifier is a hexadecimal uuid and each side stored its
        // own spelling. The caller's spelling is what is answered back.
        var asked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in providerSceneIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                asked[id] = id;
            }
        }

        var excluded = new HashSet<string>(StringComparer.Ordinal);
        if (asked.Count == 0)
        {
            return excluded;
        }

        // An answer that did not arrive, or one this could not read, excludes nothing: reporting a
        // scene as excluded on a failed read would remove it from the surface with nothing saying
        // why. The walk's own outcome is left unread here for that reason.
        await OverExclusionRowsAsync(
            baseAddress,
            apiKey,
            row =>
            {
                if (row.ForeignId is { Length: > 0 } named
                    && asked.TryGetValue(named, out var asAsked))
                {
                    excluded.Add(asAsked);
                }

                return true;
            },
            ct).ConfigureAwait(false);

        return excluded;
    }

    // Each answered row is reduced to one question, so what this holds is the caller's own set and
    // never the instance's. There is no row cap, for the reason the exclusion reduce has none.
    //
    // The body is a bare JSON array of identifier strings. An object naming the ids as a member is
    // answered 400, measured against whisparr:v3-3.3.8-release.1097.
    public async Task<IReadOnlySet<string>> ReduceHeldScenesAsync(
        Uri baseAddress,
        string apiKey,
        IReadOnlyCollection<string> foreignIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(foreignIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        // Keyed without regard to case: an identifier is a hexadecimal uuid and each side stored its
        // own spelling. The caller's spelling is what is answered back.
        var asked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in foreignIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                asked[id] = id;
            }
        }

        var held = new HashSet<string>(StringComparer.Ordinal);
        if (asked.Count == 0)
        {
            return held;
        }

        List<string> wanted = [.. asked.Values];
        var answered = await GeneratedReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IMovieApi>().CreateMovieListAsync(wanted, ct))
            .ConfigureAwait(false);

        // Raised rather than reduced to an empty set: a caller comparing its library against an
        // empty set would report every scene it asked about as one the instance does not hold.
        if (answered.StatusCode is < 200 or > 299
            || answered.Refusal is not MonitorRefusalKind.None)
        {
            throw new HttpRequestException(
                "The instance did not answer which of the asked-about scenes it holds.");
        }

        List<HeldSceneRow?>? rows;
        try
        {
            rows = JsonSerializer.Deserialize<List<HeldSceneRow?>>(answered.Body, HeldSceneRowShape);
        }
        catch (JsonException failure)
        {
            throw new HttpRequestException(
                "The instance's answer could not be read as the entries it holds.", failure);
        }

        foreach (var row in rows ?? [])
        {
            // The identifier is read off the row's stash id, falling back to its foreign id: both
            // carry the same uuid and which one an instance fills in varies.
            var named = row?.StashId is { Length: > 0 } stashed ? stashed : row?.ForeignId;
            if (named is { Length: > 0 } spelled && asked.TryGetValue(spelled, out var asAsked))
            {
                held.Add(asAsked);
            }
        }

        return held;
    }

    public async Task<SceneExclusionLookup> FindSceneExclusionAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);

        int? named = null;
        var read = await OverExclusionRowsAsync(
            baseAddress,
            apiKey,
            row =>
            {
                // Compared without regard to case, as the reduce above keys. A non-positive
                // identifier is no address the removing route could take, so such a row is skipped.
                if (row.Id >= 1
                    && string.Equals(row.ForeignId, foreignId, StringComparison.OrdinalIgnoreCase))
                {
                    named = row.Id;
                    return false;
                }

                return true;
            },
            ct).ConfigureAwait(false);

        if (!read)
        {
            return SceneExclusionLookup.DidNotComplete;
        }

        return named is { } exclusionId
            ? SceneExclusionLookup.At(exclusionId)
            : SceneExclusionLookup.NamesNoExclusion;
    }

    // Reads the exclusion list row by row until visit answers false, and answers whether a whole
    // answer arrived and could be read.
    //
    // No parameter narrows this route: a filter key and a bare foreign id are both ignored and
    // answer the whole list under a success, and a foreign id as a further segment is a not-found.
    // The answer is read as it arrives and each row is dropped before the next, so nothing here
    // grows with what the instance holds. Sent once, since nothing is retained between rows.
    private async Task<bool> OverExclusionRowsAsync(
        Uri baseAddress, string apiKey, Func<ExclusionRow, bool> visit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, RequestUri(baseAddress, ExclusionsPath));
        request.Headers.Add(ApiKeyHeader, apiKey);

        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(http.Timeout);

        try
        {
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                .ConfigureAwait(false);

            if (!IsSuccess((int)response.StatusCode))
            {
                return false;
            }

            var stream = await response.Content.ReadAsStreamAsync(attempt.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                var rows = JsonSerializer.DeserializeAsyncEnumerable<ExclusionRow>(
                    stream, ExclusionRowShape, attempt.Token);

                await foreach (var row in rows.ConfigureAwait(false))
                {
                    if (row is not null && !visit(row))
                    {
                        break;
                    }
                }
            }

            return true;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or JsonException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    public Task<WhisparrResponse> ReadHardlinkSettingAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => GeneratedReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IMediaManagementConfigApi>().GetMediaManagementConfigAsync(ct));

    // The instance is asked to include what it already holds, so a file the library holds and the
    // instance has not attached is still answered for.
    public Task<WhisparrResponse> ListImportableFilesAsync(
        Uri baseAddress, string apiKey, string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return GeneratedReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IManualImportApi>().ListManualImportAsync(
                folder: folder, filterExistingFiles: false, cancellationToken: ct));
    }

    public Task<WhisparrResponse> AttachOwnedFilesAsync(
        Uri baseAddress, string apiKey, JsonNode files, CancellationToken ct)
        => GeneratedCommandAsync(baseAddress, apiKey, ReflectOwnedPlanner.Command(files), ct);

    public Task<WhisparrResponse> ReadInstanceFolderAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        string directory,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var asDirectory = WithTrailingSeparator(directory);

        return generation switch
        {
            WhisparrGeneration.V3 => GeneratedReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V3Api.IFileSystemApi>().GetFileSystemAsync(
                    path: asDirectory,
                    includeFiles: true,
                    allowFoldersWithoutTrailingSlashes: true,
                    cancellationToken: ct)),

            WhisparrGeneration.V2 => GeneratedV2ReadAsync(
                baseAddress,
                apiKey,
                api => api.Api<V2Api.IFileSystemApi>().GetFileSystemAsync(
                    path: asDirectory,
                    includeFiles: true,
                    allowFoldersWithoutTrailingSlashes: true,
                    cancellationToken: ct)),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };
    }

    // Without a trailing separator the instance reads the spelling as a partial name and answers with
    // the names its parent holds that start with it. The separator already in the spelling is the one
    // appended, so a path rooted on a drive letter keeps its own.
    private static string WithTrailingSeparator(string directory)
    {
        if (directory.EndsWith('/') || directory.EndsWith('\\'))
        {
            return directory;
        }

        return directory + (directory.Contains('\\') ? '\\' : '/');
    }

    // The one member of this seam that can make an instance acquire anything, and the only one whose
    // invocation is recorded on its own. Its verb class has no retry entry: a second search is a
    // second download.
    //
    // v3's command names an id array and carries every id in one; v2's names a single scalar id, so
    // there it is one command per entity and the first answer that was not accepted is the one
    // reported. Either way each entity is searched once.
    public async Task<WhisparrResponse> SearchMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        IReadOnlyList<int> entityIds,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentOutOfRangeException.ThrowIfZero(entityIds.Count);
        WhisparrSyncLog.SearchIssued(log, kind);

        switch (generation)
        {
            case WhisparrGeneration.V3:
                return await GeneratedCommandAsync(
                        baseAddress, apiKey, V3BodyProjector.SearchAllMonitored(kind, entityIds), ct)
                    .ConfigureAwait(false);
            case WhisparrGeneration.V2:
                WhisparrResponse? answered = null;
                foreach (var entityId in entityIds)
                {
                    answered = await GeneratedV2GrabCommandAsync(
                            baseAddress, apiKey, V2BodyProjector.SearchMonitored(entityId), ct)
                        .ConfigureAwait(false);
                    if (MonitoringProjector.Accepted(answered) != MonitorRefusalKind.None)
                    {
                        return answered;
                    }
                }

                return answered!;
            default:
                throw new ArgumentOutOfRangeException(nameof(generation));
        }
    }

    // Recorded as the entity search is, and given no arguments: the scene, the instance and the key
    // are caller-supplied or credentials, and a log sink is durable and readable. Sent once.
    public Task<WhisparrResponse> SearchSceneAsync(
        Uri baseAddress, string apiKey, int sceneId, CancellationToken ct)
    {
        WhisparrSyncLog.SceneSearchIssued(log);

        return GeneratedCommandAsync(baseAddress, apiKey, V3BodyProjector.SearchScene(sceneId), ct);
    }

    // The identifier comes from a stored identity row rather than from a caller. The generated client
    // escapes it as one path segment, so a value carrying a separator names no other route.
    private static string Named(string foreignId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);
        return foreignId;
    }

    // Checked before any request, so a file: or ftp: address is refused rather than handed to a
    // handler that would act on it.
    internal static bool IsAddressable(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsAbsoluteUri
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);
    }

    // The settings every request through this client is made under. The timeout set here is the
    // number the send bounds a whole attempt with: the framework's own timeout ends at the headers
    // once the body is asked for separately, so the send reads this value and bounds both phases.
    internal static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = RequestTimeout;
    }

    // Certificate validation stays at its default, so a self-signed Whisparr reports as unreachable
    // rather than every instance's identity becoming unverifiable.
    internal static HttpMessageHandler CreateHandler()
        => new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = MaxRedirects,
        };

    // The read class through the generated client. Re-issued on the same failure and for the same
    // reason the hand-composed read is: a re-read creates nothing.
    private async Task<WhisparrResponse> GeneratedReadAsync<TResponse>(
        Uri baseAddress,
        string apiKey,
        Func<Whisparr3Apis, Task<TResponse>> call)
        where TResponse : V3Client.IApiResponse
    {
        var target = TargetFor(baseAddress, apiKey);
        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            try
            {
                return await GeneratedSendAsync(target, call).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is HttpRequestException or IOException)
            {
                // No whole answer arrived, which is the one failure a read may be re-issued after.
            }
        }

        return await GeneratedSendAsync(target, call).ConfigureAwait(false);
    }

    // Sent once, for the reason the hand-composed acting send is: a request whose answer did not
    // arrive is not the same as one that says nothing happened.
    private Task<WhisparrResponse> GeneratedActAsync<TResponse>(
        Uri baseAddress,
        string apiKey,
        Func<Whisparr3Apis, Task<TResponse>> call)
        where TResponse : V3Client.IApiResponse
        => GeneratedSendAsync(TargetFor(baseAddress, apiKey), call);

    // Every instance-side action this generation takes is issued through the one command route. Sent
    // once. The acting class and the grabbing class both reach the route through this send, so an
    // attempt count added here would cover the class that downloads.
    private Task<WhisparrResponse> GeneratedCommandAsync(
        Uri baseAddress, string apiKey, JsonObject command, CancellationToken ct)
    {
        var (name, payload) = VerbAndPayload(command);

        return GeneratedSendAsync(
            TargetFor(baseAddress, apiKey),
            api => api.Api<V3Api.CommandApi>().SendCommandAsync(name, payload, ct));
    }

    // The verb travels as the call's own argument, so the composed body carries it and the payload
    // does not.
    private static (string Name, JsonObject Payload) VerbAndPayload(JsonObject command)
    {
        var name = (string?)command[CommandNameProperty]
            ?? throw new ArgumentException("A command names no verb.", nameof(command));

        var payload = (JsonObject)command.DeepClone();
        payload.Remove(CommandNameProperty);
        return (name, payload);
    }

    private async Task<WhisparrResponse> GeneratedSendAsync<TResponse>(
        Whisparr3Target target,
        Func<Whisparr3Apis, Task<TResponse>> call)
        where TResponse : V3Client.IApiResponse
    {
        try
        {
            return Whisparr3Gateway.Answered(await call(v3Gateway.For(target)).ConfigureAwait(false));
        }
        catch (AnswerTooLargeException beyond)
        {
            return BeyondReadBound(target.BaseAddress, beyond);
        }
    }

    // The read class through v2's generated client, re-issued on the same failure
    // and for the same reason v3's is.
    private async Task<WhisparrResponse> GeneratedV2ReadAsync<TResponse>(
        Uri baseAddress,
        string apiKey,
        Func<Whisparr2Apis, Task<TResponse>> call,
        TimeSpan? budget = null)
        where TResponse : V2Client.IApiResponse
    {
        var target = V2TargetFor(baseAddress, apiKey, budget ?? RequestTimeout);
        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            try
            {
                return await GeneratedV2SendAsync(target, call).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is HttpRequestException or IOException)
            {
                // No whole answer arrived, which is the one failure a read may be re-issued after.
            }
        }

        return await GeneratedV2SendAsync(target, call).ConfigureAwait(false);
    }

    // Sent once, for the reason v3's acting send is: a request whose answer did not
    // arrive is not the same as one that says nothing happened.
    private Task<WhisparrResponse> GeneratedV2ActAsync<TResponse>(
        Uri baseAddress,
        string apiKey,
        Func<Whisparr2Apis, Task<TResponse>> call)
        where TResponse : V2Client.IApiResponse
        => GeneratedV2SendAsync(V2TargetFor(baseAddress, apiKey), call);

    // Sent once. Held apart from the acting sends that name the same route, so an attempt count added
    // here covers the grabbing class alone.
    private Task<WhisparrResponse> GeneratedV2GrabCommandAsync(
        Uri baseAddress, string apiKey, JsonObject command, CancellationToken ct)
    {
        var (name, payload) = VerbAndPayload(command);

        return GeneratedV2SendAsync(
            V2TargetFor(baseAddress, apiKey),
            api => api.Api<V2Api.CommandApi>().SendCommandAsync(name, payload, ct));
    }

    private async Task<WhisparrResponse> GeneratedV2SendAsync<TResponse>(
        Whisparr2Target target,
        Func<Whisparr2Apis, Task<TResponse>> call)
        where TResponse : V2Client.IApiResponse
    {
        try
        {
            return Whisparr2Gateway.Answered(await call(v2Gateway.For(target)).ConfigureAwait(false));
        }
        catch (AnswerTooLargeException beyond)
        {
            return BeyondReadBound(target.BaseAddress, beyond);
        }
    }

    private static Whisparr2Target V2TargetFor(
        Uri baseAddress, string apiKey, TimeSpan? budget = null)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return new Whisparr2Target(baseAddress, apiKey, budget ?? RequestTimeout);
    }

    private static Whisparr3Target TargetFor(Uri baseAddress, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return new Whisparr3Target(baseAddress, apiKey);
    }

    // The status the instance answered with, carried on the failure, and an empty body: the body a
    // short read produced would parse as a valid answer.
    private WhisparrResponse BeyondReadBound(Uri baseAddress, AnswerTooLargeException beyond)
    {
        WhisparrSyncLog.ResponseBeyondReadBound(log, baseAddress.Host, MaxResponseBytes);
        return new WhisparrResponse(beyond.StatusCode, null, string.Empty)
        {
            Refusal = MonitorRefusalKind.AnswerTooLargeToRead,
        };
    }

    // Re-issuing a read re-reads and can create nothing, so the read class is the only one that gets
    // more than one attempt. The last attempt is the plain send, so its failure propagates rather
    // than being counted again.
    private async Task<WhisparrResponse> ReadAsync(
        Uri baseAddress, string apiKey, string path, CancellationToken ct)
    {
        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            if (await TrySendAsync(baseAddress, apiKey, HttpMethod.Get, path, null, ct)
                .ConfigureAwait(false) is { } answered)
            {
                return answered;
            }
        }

        return await SendAsync(baseAddress, apiKey, HttpMethod.Get, path, null, ct).ConfigureAwait(false);
    }

    // Sent once. This class changes the instance's own configuration, and a request whose answer did
    // not arrive is not the same as one that says nothing happened.
    private Task<WhisparrResponse> ConfigureAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode body, CancellationToken ct)
        => SentOnceAsync(baseAddress, apiKey, method, path, body, ct);

    // Sent once for the same reason, and named apart from a configure because the retry policy is
    // keyed on the class of work: an attempt count added for one class must not cover the other.
    private Task<WhisparrResponse> ActAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode body, CancellationToken ct)
        => SentOnceAsync(baseAddress, apiKey, method, path, body, ct);

    private Task<WhisparrResponse> SentOnceAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync(baseAddress, apiKey, method, path, body, ct);
    }

    // Null when no whole answer arrived, which is the one failure a read may be re-issued after. A
    // status, however unwelcome, is an answer and is returned.
    //
    // Two failures reach that reading rather than one. A connection that never established raises
    // HttpRequestException. A body that ended before its declared length raises IOException, because
    // the body is read out of the response stream here rather than buffered inside the send, and the
    // stream reports a truncation as an I/O failure.
    //
    // An answer past the read bound is an answer too: it carries its own refusal rather than throwing,
    // so it returns here on the first attempt and is not downloaded a second time.
    private async Task<WhisparrResponse?> TrySendAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        try
        {
            return await SendAsync(baseAddress, apiKey, method, path, body, ct).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            return null;
        }
    }

    private async Task<WhisparrResponse> SendAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, RequestUri(baseAddress, path));
        request.Headers.Add(ApiKeyHeader, apiKey);
        if (body is not null)
        {
            request.Content = new StringContent(
                body.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json);
        }

        // The whole attempt is bounded here, headers and body alike. The client's own timeout stops
        // at the headers once the body is asked for separately, so a body phase left to it runs
        // until the instance itself gives up. The number is read off the client rather than restated,
        // so one setting bounds one attempt.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(http.Timeout);

        try
        {
            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                .ConfigureAwait(false);
            var contentType = response.Content.Headers.ContentType?.ToString();
            var answered = await ReadWithinBoundAsync(response.Content, attempt.Token)
                .ConfigureAwait(false);

            if (answered is null)
            {
                WhisparrSyncLog.ResponseBeyondReadBound(
                    log, request.RequestUri?.Host ?? string.Empty, MaxResponseBytes);
                return new WhisparrResponse((int)response.StatusCode, contentType, string.Empty)
                {
                    Refusal = MonitorRefusalKind.AnswerTooLargeToRead,
                };
            }

            return new WhisparrResponse((int)response.StatusCode, contentType, answered);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Reported as the framework reports its own timeout, so a caller classifying the failure
            // does not have to know where the bound lives. A shutdown fails the filter and propagates
            // as itself, which is what keeps it classified as cancelled rather than as a verdict
            // about the instance.
            throw new TaskCanceledException(
                "The request outlived the bound on one attempt.", new TimeoutException(), attempt.Token);
        }
    }

    // Null when the answer is past the bound. Read here rather than bounded by the handler, whose
    // own bound raises an exception whose type a refused connection shares. A body past the bound is
    // discarded unread and never returned.
    private static async Task<string?> ReadWithinBoundAsync(HttpContent content, CancellationToken ct)
    {
        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffered = new MemoryStream();
            var chunk = new byte[ReadChunkBytes];

            while (buffered.Length < ReadCeilingBytes)
            {
                var wanted = (int)Math.Min(chunk.Length, ReadCeilingBytes - buffered.Length);
                var read = await stream.ReadAsync(chunk.AsMemory(0, wanted), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return Decode(EncodingFor(content), buffered);
                }

                await buffered.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            return null;
        }
    }

    // Decoded without the encoding's preamble, which a framework-level string read also skips. A
    // preamble left in place puts U+FEFF at the front of the string, and every reader of a body here
    // parses it as JSON: the parse then fails and each of them answers null, so a BOM-prefixed
    // instance would read as holding nothing anywhere, with nothing saying why.
    private static string Decode(Encoding encoding, MemoryStream buffered)
    {
        var preamble = encoding.Preamble;
        var buffer = buffered.GetBuffer();
        var length = (int)buffered.Length;
        var offset = length >= preamble.Length
            && buffer.AsSpan(0, preamble.Length).SequenceEqual(preamble)
                ? preamble.Length
                : 0;

        return encoding.GetString(buffer, offset, length - offset);
    }

    // The charset the answer names, which is what a framework-level string read honours. Decoding as
    // UTF-8 unconditionally would change what a non-UTF-8 instance's answer says.
    private static Encoding EncodingFor(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    // Relative-Uri composition drops the last segment of a base that does not end in a separator,
    // which would turn a URL base of /whisparr into a request at the site root instead.
    private static Uri RequestUri(Uri baseAddress, string path)
    {
        var builder = new UriBuilder(baseAddress);
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += '/';
        }

        return new Uri(builder.Uri, path);
    }
}
