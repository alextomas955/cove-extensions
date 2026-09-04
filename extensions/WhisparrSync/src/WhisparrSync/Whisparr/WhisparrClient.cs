using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;

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
    /// A request that changes the instance's own configuration. Never re-issued: a second attempt
    /// after an answer that did not arrive would act twice.
    /// </summary>
    Configure,

    /// <summary>
    /// A request that changes what an instance monitors. Never re-issued: a second attempt after an
    /// answer that did not arrive would act twice.
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
/// <param name="StatusCode">The HTTP status.</param>
/// <param name="ContentType">
/// The <c>Content-Type</c> header as received, unparsed. A rejected key answers with none on both
/// generations, so the empty case is a real observation rather than a missing one.
/// </param>
/// <param name="Body">The response body as text; empty when there was none.</param>
public sealed record WhisparrResponse(int StatusCode, string? ContentType, string Body)
{
    /// <summary>Why no entity was named, where a seam read that out of a parsed body.</summary>
    /// <remarks>
    /// The older generation resolves an identifier through a lookup that states its answer in the
    /// body and not in the status: an identifier its own source does not know is answered with a
    /// success and an empty list. A seam reading that meaning states it here, so a caller classifies
    /// the fact rather than a status this product would otherwise have had to invent, and the two
    /// readings that mean different things to a reader stay apart.
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
/// Deliberately narrow: there is no method taking a caller-supplied path and none taking an HTTP
/// verb, so no call site can express a request that makes Whisparr search for or download anything.
/// Widening it is the decision that would have to be taken openly.
/// <para>
/// <see cref="ReadRootFoldersAsync"/> was added under that rule. It takes no caller-supplied path,
/// no caller-supplied identifier and no verb, so the constraint above still holds over the whole
/// interface.
/// </para>
/// <para>
/// <see cref="ReadHistoryAsync"/> was added under the same rule, and is a read because re-issuing it
/// reads again and grabs nothing. It names a page, a page size and a lineage; the route, the order
/// and the entity spelling belong to the seam, so no call site supplies any of them.
/// </para>
/// <para>
/// <see cref="ReadQualityProfilesAsync"/> was added under the same rule. It takes no caller-supplied
/// path, no caller-supplied identifier and no verb.
/// </para>
/// <para>
/// The verbs that change what an instance monitors were deliberately NOT added here. They are the
/// roles in <c>WhisparrSync.Monitoring</c>, and the one verb that can make an instance download is
/// alone on <c>IWhisparrSearchGrabbing</c> there. Each is obtained by name through a capability set,
/// so the constraint above stays true of this interface however far the acting surface grows.
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
    /// An add cannot be composed without one, and which profiles exist is the instance's own and not
    /// this product's to assume: a profile id it does not offer is refused by one generation and
    /// accepted by the other.
    /// </remarks>
    Task<WhisparrResponse> ReadQualityProfilesAsync(Uri baseAddress, string apiKey, CancellationToken ct);

    /// <summary>
    /// Reads one page of the instance's import history, with each record's own metadata entity.
    /// </summary>
    /// <remarks>
    /// The newest-first order is asked for and not relied on: whether the route honours the request is
    /// unmeasured, so a caller reads the page's own order and refuses one it cannot walk.
    /// <para>
    /// Which entity to embed is the one thing the generation decides here. The route, the order and
    /// the request to embed at all belong to the seam, so no call site supplies any of them.
    /// </para>
    /// </remarks>
    /// <param name="baseAddress">The instance to read from.</param>
    /// <param name="apiKey">The key that instance authenticates the read with.</param>
    /// <param name="generation">The lineage whose entity spelling the page is asked for.</param>
    /// <param name="page">Which page, counting from one.</param>
    /// <param name="pageSize">How many records that page holds at most.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="page"/> or <paramref name="pageSize"/> is below one, or
    /// <paramref name="generation"/> is not a lineage this product reads.
    /// </exception>
    Task<WhisparrResponse> ReadHistoryAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>Creates one notification.</summary>
    /// <remarks>
    /// Never re-issued on a failure, whatever the failure is. The instance enforces name uniqueness,
    /// so a second attempt after an answer that did not arrive is refused rather than duplicated -
    /// but the answer to that refusal is indistinguishable from a real one, so the re-issue is not
    /// made at all.
    /// </remarks>
    Task<WhisparrResponse> CreateNotificationAsync(
        Uri baseAddress, string apiKey, JsonNode body, CancellationToken ct);

    /// <summary>Replaces the notification with <paramref name="id"/>.</summary>
    /// <inheritdoc cref="CreateNotificationAsync" path="/remarks"/>
    Task<WhisparrResponse> UpdateNotificationAsync(
        Uri baseAddress, string apiKey, int id, JsonNode body, CancellationToken ct);
}

/// <inheritdoc cref="IWhisparrClient"/>
/// <remarks>
/// The acting roles are implemented here rather than on a type of their own, because this is the one
/// type holding an HTTP client and a second holder would be a second outbound surface for every
/// invariant that reflects over this one to cover.
/// </remarks>
internal sealed class WhisparrClient(HttpClient http, ILogger log)
    : IWhisparrClient,
        IWhisparrStudioActing,
        IWhisparrPerformerActing,
        IWhisparrMissingSceneActing,
        IWhisparrReflectOwnedActing,
        IWhisparrSearchGrabbing
{
    /// <summary>The header both generations authenticate an API request with.</summary>
    internal const string ApiKeyHeader = "X-Api-Key";

    // Relative, so they compose onto a base address carrying a URL base (a reverse-proxy subpath).
    // Both generations serve the v3 route family; the version in the path is not the generation.
    private const string StatusPath = "api/v3/system/status";
    private const string NotificationPath = "api/v3/notification";
    private const string NotificationSchemaPath = "api/v3/notification/schema";
    private const string RootFolderPath = "api/v3/rootfolder";
    private const string HistoryPath = "api/v3/history";
    private const string QualityProfilePath = "api/v3/qualityprofile";

    // Every route this product can issue is declared on this type, whichever role issues it. The
    // route invariant reads this type's own literals, so a constant declared anywhere else is
    // invisible to it and the transcribed set it is compared against would still agree.
    internal const string StudioPath = "api/v3/studio";
    internal const string StudioEditorPath = "api/v3/studio/editor";
    internal const string PerformerPath = "api/v3/performer";
    internal const string PerformerEditorPath = "api/v3/performer/editor";
    internal const string SeriesPath = "api/v3/series";
    internal const string SeriesLookupPath = "api/v3/series/lookup";
    internal const string SeriesEditorPath = "api/v3/series/editor";
    internal const string SeasonPassPath = "api/v3/seasonpass";
    internal const string CommandPath = "api/v3/command";
    internal const string MoviePath = "api/v3/movie";
    internal const string ManualImportPath = "api/v3/manualimport";
    internal const string MediaManagementConfigPath = "api/v3/config/mediamanagement";

    // The one status this product composes rather than receives, and the only one anywhere in it.
    // The older generation answers "do you hold this entity" through no single route, so that reading
    // is assembled from a lookup and a listing and reported in the spelling a caller already
    // classifies. Named rather than written inline so a reader is not left to infer that an instance
    // sent it.
    private const int AssembledNotHeld = 404;

    // The order belongs to the verb rather than to a call: newest-first is the only order a walk that
    // stops at a stored position can read, and a call site free to spell it could ask for another.
    private const string NewestFirstQuery = "sortKey=date&sortDirection=descending";

    // Each lineage names its own metadata entity on this one route, and that entity is where the
    // identifier the two ingest channels agree on lives. Asked for on the same request rather than
    // through a second one, so what a page costs does not grow with what it holds.
    private const string V3EntityQuery = "includeMovie=true";
    private const string V2EntityQuery = "includeEpisode=true";

    // The field the older generation's own lookup answers an entity's numeric id in. It is misnamed
    // after an unrelated television database and names no such thing here.
    private const string SeriesByEntityIdQuery = "tvdbId";

    /// <summary>How long one attempt may take before it is reported as unreachable.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many redirects the client follows. A login redirect is a real deployment, and following an
    /// unbounded chain of them is not.
    /// </summary>
    internal const int MaxRedirects = 3;

    /// <summary>How much of one answer the client will hold in memory before refusing it.</summary>
    /// <remarks>
    /// Exceeding it fails the read rather than yielding a short body, which would parse as a valid
    /// page.
    /// </remarks>
    internal const long MaxResponseBytes = 8L * 1024 * 1024;

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

        return await ReadAsync(baseAddress, apiKey, StatusPath, ct).ConfigureAwait(false);
    }

    public Task<WhisparrResponse> ReadNotificationSchemaAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => ReadAsync(baseAddress, apiKey, NotificationSchemaPath, ct);

    public Task<WhisparrResponse> ListNotificationsAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => ReadAsync(baseAddress, apiKey, NotificationPath, ct);

    public Task<WhisparrResponse> ReadRootFoldersAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => ReadAsync(baseAddress, apiKey, RootFolderPath, ct);

    public Task<WhisparrResponse> ReadQualityProfilesAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => ReadAsync(baseAddress, apiKey, QualityProfilePath, ct);

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

        return ReadAsync(
            baseAddress,
            apiKey,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{HistoryPath}?page={page}&pageSize={pageSize}&{NewestFirstQuery}&{EntityQueryFor(generation)}"),
            ct);
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
            WhisparrGeneration.V3 => ReadEntityAsync(baseAddress, apiKey, StudioPath, foreignId, ct),
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
            WhisparrGeneration.V3 => ActAsync(
                baseAddress,
                apiKey,
                HttpMethod.Post,
                StudioPath,
                V3BodyProjector.AddStudio(foreignId, scope, defaults, DateTimeOffset.UtcNow),
                ct),
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
            WhisparrGeneration.V3 => ActAsync(
                baseAddress,
                apiKey,
                HttpMethod.Put,
                StudioEditorPath,
                V3BodyProjector.SetStudioMonitored(entityId, monitored),
                ct),
            WhisparrGeneration.V2 => ActAsync(
                baseAddress,
                apiKey,
                HttpMethod.Put,
                SeriesEditorPath,
                V2BodyProjector.SetMonitored(entityId, monitored),
                ct),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

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
            WhisparrGeneration.V2 => ActAsync(
                baseAddress,
                apiKey,
                HttpMethod.Post,
                SeasonPassPath,
                V2BodyProjector.SetScope(entityId, scope),
                ct),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    private async Task<WhisparrResponse> SetStudioDateGateAsync(
        Uri baseAddress, string apiKey, int entityId, MonitorScope scope, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);

        // Read then replaced, because the editor resource declares no add-time date gate: a scope
        // sent there is accepted and applies nothing. The read is idempotent and the replace is sent
        // once.
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

    /// <summary>Whether the older generation's instance holds the entity named by an identifier.</summary>
    /// <remarks>
    /// Two reads, because this generation answers the question through no single route: its lookup
    /// resolves the identifier to an entity and carries no instance-side id until that entity has been
    /// added, and its own listing is what says whether it has been. The second read names the one
    /// entity the lookup resolved, so what it answers does not vary with how much the instance holds,
    /// and only the matched entry is carried onward.
    /// <para>
    /// The query value is the numeric id the lookup answered and is not escaped: an int has no
    /// representation carrying a separator, so escaping it would imply it could name another route.
    /// <see cref="ReadEntityAsync"/> escapes its own identifier because that one is a string.
    /// </para>
    /// </remarks>
    private async Task<WhisparrResponse> ReadHeldSeriesAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
    {
        var resolved = await ResolveSiteAsync(baseAddress, apiKey, foreignId, ct).ConfigureAwait(false);
        if (resolved.Site is not { } site)
        {
            return resolved.Answer;
        }

        var listed = await ReadAsync(
            baseAddress,
            apiKey,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{SeriesPath}?{SeriesByEntityIdQuery}={site.EntityId}"),
            ct).ConfigureAwait(false);
        if (!IsSuccess(listed.StatusCode))
        {
            return listed;
        }

        return V2LookupProjector.HeldEntry(listed.Body, site.EntityId) is { } held
            ? new WhisparrResponse(listed.StatusCode, listed.ContentType, held.ToJsonString())
            : new WhisparrResponse(AssembledNotHeld, listed.ContentType, string.Empty);
    }

    private async Task<WhisparrResponse> AddMonitoredSeriesAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        MonitorScope scope,
        AddDefaults defaults,
        CancellationToken ct)
    {
        var resolved = await ResolveSiteAsync(baseAddress, apiKey, foreignId, ct).ConfigureAwait(false);
        if (resolved.Site is not { } site)
        {
            return resolved.Answer;
        }

        return await ActAsync(
            baseAddress,
            apiKey,
            HttpMethod.Post,
            SeriesPath,
            V2BodyProjector.AddStudio(site.EntityId, site.Title, site.TitleSlug, scope, defaults),
            ct).ConfigureAwait(false);
    }

    /// <summary>The entity an identifier names on the older generation, or the answer standing for it.</summary>
    /// <remarks>
    /// The answer never echoes the term, so exactly one result is what the correspondence rests on. A
    /// second result is refused rather than picked from, because nothing in the answer says which of
    /// them was meant and acting on either would act on an entity nobody named.
    /// </remarks>
    private async Task<(V2Site? Site, WhisparrResponse Answer)> ResolveSiteAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
    {
        var lookup = await ReadAsync(
            baseAddress,
            apiKey,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{SeriesLookupPath}?term={Uri.EscapeDataString(V2BodyProjector.LookupTerm(foreignId))}"),
            ct).ConfigureAwait(false);

        if (!IsSuccess(lookup.StatusCode))
        {
            return (null, lookup);
        }

        var resolution = V2LookupProjector.Resolve(lookup.Body);
        if (resolution.Reading == V2LookupReading.Ambiguous)
        {
            WhisparrSyncLog.EntityLookupNotDistinct(log, WhisparrGeneration.V2);
        }

        if (resolution.Site is { } site)
        {
            return (site, lookup);
        }

        // Which refusal this is comes from the parsed answer, because the status carries none: an
        // identifier this generation's source does not know is answered with a success. Nothing of
        // the body is carried onward, so no sentence a reader is shown can be composed from it.
        return (null, new WhisparrResponse(lookup.StatusCode, lookup.ContentType, string.Empty)
        {
            Refusal = V2LookupProjector.RefusalFor(resolution.Reading),
        });
    }

    private static bool IsSuccess(int statusCode) => statusCode is >= 200 and < 300;

    public Task<WhisparrResponse> ReadPerformerAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
        => ReadEntityAsync(baseAddress, apiKey, PerformerPath, foreignId, ct);

    public Task<WhisparrResponse> AddMonitoredPerformerAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => ActAsync(
            baseAddress,
            apiKey,
            HttpMethod.Post,
            PerformerPath,
            V3BodyProjector.AddPerformer(foreignId, defaults),
            ct);

    public Task<WhisparrResponse> SetPerformerMonitoredAsync(
        Uri baseAddress, string apiKey, int entityId, bool monitored, CancellationToken ct)
        => ActAsync(
            baseAddress,
            apiKey,
            HttpMethod.Put,
            PerformerEditorPath,
            V3BodyProjector.SetPerformerMonitored(entityId, monitored),
            ct);

    public Task<WhisparrResponse> AddSceneAsync(
        Uri baseAddress,
        string apiKey,
        string foreignId,
        AddDefaults defaults,
        CancellationToken ct)
        => ActAsync(
            baseAddress,
            apiKey,
            HttpMethod.Post,
            MoviePath,
            V3BodyProjector.AddScene(foreignId, defaults),
            ct);

    public Task<WhisparrResponse> RefreshCatalogueAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrEntityKind kind,
        int entityId,
        CancellationToken ct)
        => ActAsync(
            baseAddress,
            apiKey,
            HttpMethod.Post,
            CommandPath,
            V3BodyProjector.RefreshCatalogue(kind, entityId),
            ct);

    public Task<WhisparrResponse> ReadHardlinkSettingAsync(
        Uri baseAddress, string apiKey, CancellationToken ct)
        => ReadAsync(baseAddress, apiKey, MediaManagementConfigPath, ct);

    // The folder travels as a query value and is escaped as one, so a directory name carrying a
    // separator names no other route. The instance is asked to include what it already holds, so a
    // file the library holds and the instance has not attached is still answered for.
    public Task<WhisparrResponse> ListImportableFilesAsync(
        Uri baseAddress, string apiKey, string folder, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        return ReadAsync(
            baseAddress,
            apiKey,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{ManualImportPath}?folder={Uri.EscapeDataString(folder)}&filterExistingFiles=false"),
            ct);
    }

    public Task<WhisparrResponse> AttachOwnedFilesAsync(
        Uri baseAddress, string apiKey, JsonNode files, CancellationToken ct)
        => ActAsync(
            baseAddress, apiKey, HttpMethod.Post, CommandPath, ReflectOwnedPlanner.Command(files), ct);

    // The one member of this whole seam that can make an instance acquire anything, and the only one
    // whose invocation is recorded on its own. Its verb class has no retry entry, so an attempt whose
    // answer did not arrive is reported rather than re-issued: a second search is a second download.
    public Task<WhisparrResponse> SearchMonitoredAsync(
        Uri baseAddress,
        string apiKey,
        WhisparrGeneration generation,
        WhisparrEntityKind kind,
        int entityId,
        CancellationToken ct)
    {
        var body = generation switch
        {
            WhisparrGeneration.V3 => V3BodyProjector.SearchMonitored(kind, entityId),
            WhisparrGeneration.V2 => V2BodyProjector.SearchMonitored(entityId),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

        WhisparrSyncLog.SearchIssued(log, kind);
        return GrabAsync(baseAddress, apiKey, HttpMethod.Post, CommandPath, body, ct);
    }

    // Escaped as one path segment. The identifier comes from a stored identity row rather than from a
    // caller, and escaping it keeps that true of the composed route as well: a value carrying a
    // separator would otherwise name a different route.
    private Task<WhisparrResponse> ReadEntityAsync(
        Uri baseAddress, string apiKey, string entityPath, string foreignId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(foreignId);

        return ReadAsync(
            baseAddress,
            apiKey,
            string.Create(
                CultureInfo.InvariantCulture, $"{entityPath}/{Uri.EscapeDataString(foreignId)}"),
            ct);
    }

    /// <summary>Which entity <paramref name="generation"/> is asked to embed on a history record.</summary>
    private static string EntityQueryFor(WhisparrGeneration generation)
        => generation switch
        {
            WhisparrGeneration.V3 => V3EntityQuery,
            WhisparrGeneration.V2 => V2EntityQuery,
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };

    /// <summary>Whether <paramref name="address"/> is one a socket may be opened to.</summary>
    /// <remarks>
    /// Checked before any request so a <c>file:</c> or <c>ftp:</c> address is refused rather than
    /// handed to a handler that would act on it.
    /// </remarks>
    internal static bool IsAddressable(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsAbsoluteUri
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>Applies the settings every request through this client is made under.</summary>
    internal static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = RequestTimeout;
        client.MaxResponseContentBufferSize = MaxResponseBytes;
    }

    /// <summary>The handler every request through this client is made through.</summary>
    /// <remarks>
    /// Certificate validation stays at its default. A self-signed Whisparr therefore reports as
    /// unreachable, which is an answer the user can act on; a bypass would make every instance's
    /// identity unverifiable to buy it.
    /// </remarks>
    internal static HttpMessageHandler CreateHandler()
        => new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = MaxRedirects,
        };

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

    // Sent once for the same reason, and named apart from a configure because the CLASS of work is
    // what the retry policy is keyed on: an attempt count added for one class must not silently
    // cover the other.
    private Task<WhisparrResponse> ActAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode body, CancellationToken ct)
        => SentOnceAsync(baseAddress, apiKey, method, path, body, ct);

    // Sent once, and named apart from the acting send because the CLASS of work is what the retry
    // policy is keyed on: an attempt count added for the acting class must not silently cover the one
    // class that downloads.
    private Task<WhisparrResponse> GrabAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode body, CancellationToken ct)
        => SentOnceAsync(baseAddress, apiKey, method, path, body, ct);

    private Task<WhisparrResponse> SentOnceAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return SendAsync(baseAddress, apiKey, method, path, body, ct);
    }

    // Null when the connection never established, which is the one failure a read may be re-issued
    // after. A status, however unwelcome, is an answer and is returned.
    private async Task<WhisparrResponse?> TrySendAsync(
        Uri baseAddress, string apiKey, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        try
        {
            return await SendAsync(baseAddress, apiKey, method, path, body, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
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

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var answered = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return new WhisparrResponse(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString(),
            answered);
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
