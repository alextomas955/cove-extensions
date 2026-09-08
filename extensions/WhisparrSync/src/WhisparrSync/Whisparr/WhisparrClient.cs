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
    /// <exception cref="IOException">
    /// The response ended before the length it declared. The body is read out of the response stream
    /// rather than buffered inside the send, so a connection dropped part way through one raises this
    /// rather than <see cref="HttpRequestException"/>. Every caller that contains one contains the
    /// other, because both mean no whole answer arrived.
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

    /// <summary>Reads the command <paramref name="commandId"/> names back off the instance.</summary>
    /// <remarks>
    /// Proves that the instance holds the command, which is what a caller that just posted one can
    /// establish. It proves nothing about anything being downloaded: the command's own progress is
    /// the instance's business, and no answer here says a release was taken.
    /// <para>
    /// A read, so re-issuing it reads again and grabs nothing. The identifier comes off the answer
    /// to the post rather than from a caller.
    /// </para>
    /// </remarks>
    Task<WhisparrResponse> ReadCommandAsync(
        Uri baseAddress, string apiKey, int commandId, CancellationToken ct);

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
/// type holding an outbound surface and a second holder would be a second one for every invariant
/// that reflects over this one to cover.
/// <para>
/// Each generation's requests are composed by its own generated client, the newer through
/// <see cref="Whisparr3Gateway"/> and the older through <see cref="Whisparr2Gateway"/>. The routes
/// still declared here are sent through the held <see cref="HttpClient"/>.
/// </para>
/// </remarks>
internal sealed class WhisparrClient(
    HttpClient http, Whisparr3Gateway v3Gateway, Whisparr2Gateway v2Gateway, ILogger log)
    : IWhisparrClient,
        IWhisparrStudioActing,
        IWhisparrPerformerActing,
        IWhisparrMissingSceneActing,
        IWhisparrReflectOwnedActing,
        IWhisparrSearchGrabbing,
        IWhisparrSceneSearchGrabbing,
        IWhisparrSceneStatusReading,
        IWhisparrSceneExclusionReading,
        IWhisparrSceneMonitorActing,
        IWhisparrSceneExclusionActing
{
    /// <summary>The header both generations authenticate an API request with.</summary>
    internal const string ApiKeyHeader = "X-Api-Key";

    // Relative, so they compose onto a base address carrying a URL base (a reverse-proxy subpath).
    // Both generations serve the v3 route family; the version in the path is not the generation.
    private const string NotificationPath = "api/v3/notification";

    // Every route this product composes itself is declared on this type, whichever role issues it.
    // The route invariant reads this type's own literals, so a constant declared anywhere else is
    // invisible to it and the transcribed set it is compared against would still agree. The routes
    // the generated client composes are not literals here, and the invariant names them separately.
    internal const string StudioPath = "api/v3/studio";
    internal const string ExclusionsPath = "api/v3/exclusions";

    // The one status this product composes rather than receives, and the only one anywhere in it.
    // The older generation answers "do you hold this entity" through no single route, so that reading
    // is assembled from a lookup and a listing and reported in the spelling a caller already
    // classifies. Named rather than written inline so a reader is not left to infer that an instance
    // sent it.
    private const int AssembledNotHeld = 404;

    // The member naming the verb on a composed command body.
    private const string CommandNameProperty = "name";

    // The order belongs to the verb rather than to a call: newest-first is the only order a walk that
    // stops at a stored position can read, and a call site free to spell it could ask for another.
    private const string NewestFirstSortKey = "date";

    /// <summary>How long one attempt may take before it is reported as unreachable.</summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many redirects the client follows. A login redirect is a real deployment, and following an
    /// unbounded chain of them is not.
    /// </summary>
    internal const int MaxRedirects = 3;

    /// <summary>How much of one answer the client will hold in memory before refusing it.</summary>
    /// <remarks>
    /// Exceeding it answers <see cref="MonitorRefusalKind.AnswerTooLargeToRead"/> with an empty body,
    /// rather than a short body, which would parse as a valid page.
    /// </remarks>
    internal const long MaxResponseBytes = 8L * 1024 * 1024;

    // One byte past the bound is what tells an answer at the bound from one over it, so the read stops
    // there rather than buffering whatever else arrived.
    private const long ReadCeilingBytes = MaxResponseBytes + 1;

    private const int ReadChunkBytes = 64 * 1024;

    private static readonly JsonSerializerOptions ExclusionRowShape = new(JsonSerializerDefaults.Web);

    // The two members of an exclusion row this product reads. Declared with no others so a row costs
    // one small object that is dropped again before the next is read. The identifier is the row's
    // own, which is what the removing route addresses; the foreign id is the scene's.
    private sealed record ExclusionRow(int Id, string? ForeignId);

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

            // Each generation names its own metadata entity on this one route, and that entity is
            // where the identifier the two ingest channels agree on lives. Asked for on the same
            // request rather than through a second one, so what a page costs does not grow with what
            // it holds.
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

    // The field-scoped patch, whose body carries only what changes. A whole-resource replace would
    // write back a resource read a moment earlier, dropping whatever the read did not answer with.
    public Task<WhisparrResponse> SetSceneMonitoredAsync(
        Uri baseAddress, string apiKey, int sceneId, bool monitored, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sceneId, 1);

        return GeneratedActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V3Api.IMovieApi>().PatchMovieByIdAsync(
                sceneId, V3BodyProjector.SceneMonitorPatch(monitored), ct));
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

    /// <summary>Whether the older generation's instance holds the entity named by an identifier.</summary>
    /// <remarks>
    /// Two reads, because this generation answers the question through no single route: its lookup
    /// resolves the identifier to an entity and carries no instance-side id until that entity has been
    /// added, and its own listing is what says whether it has been. The second read names the one
    /// entity the lookup resolved, so what it answers does not vary with how much the instance holds,
    /// and only the matched entry is carried onward.
    /// </remarks>
    private async Task<WhisparrResponse> ReadHeldSeriesAsync(
        Uri baseAddress, string apiKey, string foreignId, CancellationToken ct)
    {
        var resolved = await ResolveSiteAsync(baseAddress, apiKey, foreignId, ct).ConfigureAwait(false);
        if (resolved.Site is not { } site)
        {
            return resolved.Answer;
        }

        var listed = await GeneratedV2ReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.ISeriesApi>().ListSeriesAsync(
                tvdbId: site.EntityId, cancellationToken: ct)).ConfigureAwait(false);
        if (Refused(listed))
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

        return await GeneratedV2ActAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.ISeriesApi>().CreateSeriesAsync(
                V2BodyProjector.AddStudio(site.EntityId, site.Title, site.TitleSlug, scope, defaults),
                ct)).ConfigureAwait(false);
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
        var lookup = await GeneratedV2ReadAsync(
            baseAddress,
            apiKey,
            api => api.Api<V2Api.ISeriesLookupApi>().ListSeriesLookupAsync(
                V2BodyProjector.LookupTerm(foreignId), ct)).ConfigureAwait(false);

        if (Refused(lookup))
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

    // One key, single-valued. Repeating it answers only the first value's row, comma-joining answers
    // nothing, and the two plural spellings this instance accepts are ignored and answer with the
    // whole catalogue. A page built on either would look right on a small instance.
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

        // Keyed without regard to case, because an identifier is a hexadecimal uuid and the two
        // sides spell one in whichever case each stored it. The caller's own spelling is what is
        // answered, so nothing downstream has to match a spelling this read chose.
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

        // An answer that did not arrive, or one this could not read, excludes nothing. Reporting a
        // scene as excluded on the strength of a failed read would remove it from the surface with
        // nothing saying why, so the walk's own outcome is deliberately left unread here.
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
                // Compared without regard to case for the reason the reduce above keys that way. A
                // non-positive identifier is no address the removing route could take, so a row
                // carrying one is passed over rather than answered.
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

    /// <summary>
    /// Reads the instance's exclusion list row by row, until <paramref name="visit"/> answers false.
    /// </summary>
    /// <remarks>
    /// No parameter narrows this route, so none is composed. A filter key, a bare foreign id and a
    /// foreign id as a further segment were each measured against the instance: the first two are
    /// ignored and answer the whole list under a success, and the third is a not-found. An ignored
    /// parameter answering a success is indistinguishable from one that narrowed.
    /// <para>
    /// The answer is therefore read as it arrives and each row is dropped again before the next is
    /// read, so nothing here grows with what the instance holds.
    /// </para>
    /// <para>
    /// Sent once. Nothing is retained between rows, so a failure part way through has already
    /// discarded what it read, and a re-issue would transfer the whole list a second time to answer
    /// the same question.
    /// </para>
    /// </remarks>
    /// <returns>Whether a whole answer arrived and could be read.</returns>
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
        WhisparrSyncLog.SearchIssued(log, kind);

        return generation switch
        {
            WhisparrGeneration.V3 => GeneratedCommandAsync(
                baseAddress, apiKey, V3BodyProjector.SearchMonitored(kind, entityId), ct),
            WhisparrGeneration.V2 => GeneratedV2GrabCommandAsync(
                baseAddress, apiKey, V2BodyProjector.SearchMonitored(entityId), ct),
            _ => throw new ArgumentOutOfRangeException(nameof(generation)),
        };
    }

    // Recorded for the reason the entity search is, and given nothing at all: which scene, which
    // instance and which key are either caller-supplied or credentials, and a log sink is durable and
    // readable. Sent once, so an attempt whose answer did not arrive is reported rather than
    // re-issued.
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
    /// <remarks>
    /// The response bound is applied inside the send rather than here. A handler-level bound raises
    /// <see cref="HttpRequestException"/>, which this product's failure classification reduces to a
    /// type name a refused connection produces too, so a caller could not tell this product's own
    /// limit from an instance it never reached.
    /// <para>
    /// The timeout set here is the number the send bounds a whole attempt with, not only the number
    /// the framework applies. The framework's own timeout ends at the headers once the body is asked
    /// for separately, so the send reads this value and bounds both phases with it.
    /// </para>
    /// </remarks>
    internal static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = RequestTimeout;
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

    // The read class through the older generation's generated client, re-issued on the same failure
    // and for the same reason the newer generation's is.
    private async Task<WhisparrResponse> GeneratedV2ReadAsync<TResponse>(
        Uri baseAddress,
        string apiKey,
        Func<Whisparr2Apis, Task<TResponse>> call)
        where TResponse : V2Client.IApiResponse
    {
        var target = V2TargetFor(baseAddress, apiKey);
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

    // Sent once, for the reason the newer generation's acting send is: a request whose answer did not
    // arrive is not the same as one that says nothing happened.
    private Task<WhisparrResponse> GeneratedV2ActAsync<TResponse>(
        Uri baseAddress,
        string apiKey,
        Func<Whisparr2Apis, Task<TResponse>> call)
        where TResponse : V2Client.IApiResponse
        => GeneratedV2SendAsync(V2TargetFor(baseAddress, apiKey), call);

    // Sent once. The grabbing class is the only one that reaches this generation's command route, so
    // an attempt count added here covers no other class.
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

    private static Whisparr2Target V2TargetFor(Uri baseAddress, string apiKey)
    {
        ArgumentNullException.ThrowIfNull(baseAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        return new Whisparr2Target(baseAddress, apiKey);
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

    // Sent once for the same reason, and named apart from a configure because the CLASS of work is
    // what the retry policy is keyed on: an attempt count added for one class must not silently
    // cover the other.
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

    // Null when the answer is past the bound. Read here rather than bounded by the handler, so the
    // limit is one this product can name: the handler's own bound raises an exception whose type a
    // refused connection shares, and no caller could tell the two apart.
    //
    // The body of an answer past the bound is discarded unread beyond the ceiling and never returned.
    // A refused add on one generation answers with a full stack trace, so the value the bound was
    // passed reading is exactly the value that must not travel.
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
