using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace WhisparrSync.Client;

/// <summary>
/// Transport-only typed client for a Whisparr v3 instance. Attaches the <c>X-Api-Key</c> header, applies
/// a per-call timeout, and — before deserializing — guards the status code and <c>Content-Type</c>, then
/// classifies the outcome into a typed <see cref="WhisparrResult{T}"/> instead of throwing. Idempotent
/// GETs retry a bounded number of times on a transient transport fault; the non-idempotent webhook POST is
/// single-shot. Holds no Whisparr-shape knowledge beyond the endpoint paths + DTOs; all version/domain
/// decisions live above it (the adapter).
/// </summary>
internal sealed class WhisparrClient(HttpClient http)
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    // Idempotent GETs may re-issue once on a transient transport fault; a non-idempotent POST is never
    // blind-retried (a retried notification POST could double-register a webhook).
    private const int GetMaxAttempts = 2;
    private const int PostMaxAttempts = 1;

    // A not-added performer/studio GET is answered 404 by this v3.3.x build and 500 by older
    // builds (stasharr's "HTTP 500 = not-found"). BOTH are a classified Absent data outcome, never
    // Unreachable — keyed on the status line so a problem+json body still classifies here.
    private static readonly IReadOnlySet<HttpStatusCode> AbsentGetCodes =
        new HashSet<HttpStatusCode> { HttpStatusCode.NotFound, HttpStatusCode.InternalServerError };

    // A create POST of an already-existing entity surfaces 409 on builds that reject the
    // duplicate; classify it as a non-error Conflict the caller re-reads. (Other builds answer a 2xx
    // with the existing row, which classifies as Ok — both are success, never a transport failure.)
    private static readonly IReadOnlySet<HttpStatusCode> CreateConflictCodes =
        new HashSet<HttpStatusCode> { HttpStatusCode.Conflict };

    // The exclusion DELETE is idempotent — a 2xx removes the row, and a 404 means the row was
    // already gone (a concurrent remove, or never excluded). BOTH are the same success outcome the caller
    // treats as "not excluded now"; the empty/non-JSON body of a DELETE bypasses the Content-Type guard.
    private static readonly IReadOnlySet<HttpStatusCode> DeleteIdempotentCodes =
        new HashSet<HttpStatusCode>
        {
            HttpStatusCode.OK,
            HttpStatusCode.NoContent,
            HttpStatusCode.Accepted,
            HttpStatusCode.NotFound,
        };

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/system/status</c>. Never throws for a transport/HTTP fault: 401/403 →
    /// <see cref="WhisparrResultState.BadKey"/>, a non-JSON body (reverse-proxy HTML/502) →
    /// <see cref="WhisparrResultState.NotWhisparr"/>, a timeout/refused connection →
    /// <see cref="WhisparrResultState.Unreachable"/>, otherwise <see cref="WhisparrResultState.Ok"/>.
    /// </summary>
    internal Task<WhisparrResult<SystemStatus>> GetStatusAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/system/status"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.SystemStatus, token),
            ct);

    /// <summary>Reads <c>GET {baseUrl}/api/v3/rootfolder</c> (idempotent; bounded retry). Transport-only.</summary>
    internal Task<WhisparrResult<RootFolder[]>> ListRootFoldersAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/rootfolder"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.RootFolderArray, token),
            ct);

    /// <summary>Reads <c>GET {baseUrl}/api/v3/qualityprofile</c> (idempotent; bounded retry). Transport-only.</summary>
    internal Task<WhisparrResult<QualityProfile[]>> ListQualityProfilesAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/qualityprofile"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.QualityProfileArray, token),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/movie</c> — the full movie set, unpaged (issue #218), for
    /// reconciliation (idempotent; bounded retry). Transport-only: the classify-not-throw guards are
    /// inherited from <see cref="SendAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<WhisparrMovie[]>> ListMoviesAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/movie"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrMovieArray, token),
            ct);

    /// <summary>
    /// Reads one page of <c>GET {baseUrl}/api/v3/history</c> newest-first — the reconcile backstop's
    /// data source (idempotent; bounded retry). The caller pages until it reaches the stored checkpoint, so a
    /// full history is never pulled at once. Transport-only: the classify-not-throw guards are inherited from
    /// <see cref="SendAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<WhisparrHistoryPage>> ListHistoryAsync(
        string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl.TrimEnd('/')}/api/v3/history?page={page}&pageSize={pageSize}&sortKey=date&sortDirection=descending"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrHistoryPage, token),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/series</c> — the Whisparr v2 studio/site list (idempotent; bounded
    /// retry). The v2 adapter walks these into scenes; v2 has no <c>/movie</c> entity. Transport-only: the
    /// classify-not-throw guards are inherited from <see cref="SendAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<WhisparrSeries[]>> ListSeriesAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/series"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrSeriesArray, token),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/episode?seriesId={seriesId}</c> — the scenes under a v2 series (the
    /// <c>seriesId</c> query is REQUIRED; idempotent, bounded retry). Transport-only: the classify-not-throw
    /// guards are inherited from <see cref="SendAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<WhisparrEpisode[]>> ListEpisodesAsync(string baseUrl, string apiKey, int seriesId, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/episode?seriesId={seriesId}"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrEpisodeArray, token),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/episodefile?seriesId={seriesId}</c> — the on-disk files for a v2 series
    /// (the <c>seriesId</c> query is REQUIRED; idempotent, bounded retry). Source of the path leg. Transport-
    /// only: the classify-not-throw guards are inherited from <see cref="SendAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<WhisparrEpisodeFile[]>> ListEpisodeFilesAsync(string baseUrl, string apiKey, int seriesId, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/episodefile?seriesId={seriesId}"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrEpisodeFileArray, token),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/manualimport?folder={folder}&amp;filterExistingFiles=false</c> — the
    /// candidate rows Whisparr sees under <paramref name="folder"/> (idempotent; bounded retry), the input to a
    /// targeted in-place <c>ManualImport</c>. <c>filterExistingFiles=false</c> is REQUIRED so an already-known
    /// file still lists (an owned-scene import re-attaches a file Whisparr can already see). The adapter matches
    /// the owned file to a row by path and reuses that row's quality/languages verbatim. Transport-only.
    /// </summary>
    internal Task<WhisparrResult<WhisparrManualImportItem[]>> ListManualImportAsync(
        string baseUrl, string apiKey, string folder, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl.TrimEnd('/')}/api/v3/manualimport?folder={Uri.EscapeDataString(folder)}&filterExistingFiles=false"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrManualImportItemArray, token),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/series/lookup?term={term}</c> — the Whisparr v2 site (series) search
    /// (idempotent; bounded retry). The v2 outward path resolves a site by its TPDB id via the term
    /// <c>tpdb:{id}</c>; a matched row carries that id in the Sonarr-style <c>tvdbId</c> slot. Returns the
    /// (possibly empty) <see cref="WhisparrSeries"/> array; an empty array is a valid
    /// <see cref="WhisparrResultState.Ok"/> meaning "no match". The adapter owns the term. Transport-only.
    /// </summary>
    internal Task<WhisparrResult<WhisparrSeries[]>> LookupSeriesAsync(string baseUrl, string apiKey, string term, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl.TrimEnd('/')}/api/v3/series/lookup?term={Uri.EscapeDataString(term)}"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrSeriesArray, token),
            ct);

    /// <summary>
    /// Posts the caller-serialized series body to <c>POST {baseUrl}/api/v3/series</c> — the v2 site add
    /// (single-shot; a non-idempotent POST is never blind-retried). A 2xx returns the created
    /// <see cref="WhisparrSeries"/>; an already-added site is classified <see cref="WhisparrResultState.Conflict"/>
    /// for the caller to re-read — never a transport failure. The adapter owns the payload shape (the
    /// <c>tvdbId</c>/monitored/addOptions body); the loop-safety non-grab keys live there.
    /// </summary>
    /// <remarks>
    /// v2 (Sonarr-based) signals a DUPLICATE add with an HTTP 400 validation body naming the
    /// <c>SeriesExistsValidator</c> ("This series has already been added") rather than a 409 — the v2 analogue
    /// of Eros's <c>MovieExistsValidator</c>. The idempotency spine recognises that 400-with-exists body as the
    /// same non-error Conflict a 409 yields, or a re-add mis-classifies as Unreachable. A genuine bad-body 400
    /// (no exists marker) still classifies as Unreachable.
    /// </remarks>
    internal Task<WhisparrResult<WhisparrSeries>> CreateSeriesAsync(string baseUrl, string apiKey, string seriesJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/v3/series")
            {
                Content = new StringContent(seriesJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrSeries, token),
            ct,
            conflictCodes: CreateConflictCodes,
            conflictBodyMatch: IsSeriesAlreadyAddedBody);

    // v2 signals a duplicate series ADD with an HTTP 400 SeriesExistsValidator body rather than a 409.
    // Recognise it by body so a re-add resolves to the same non-error Conflict a 409 yields — the
    // idempotency contract. A genuine bad-body 400 (no exists marker) still falls through to Unreachable.
    private static bool IsSeriesAlreadyAddedBody(string body)
        => body.Contains("SeriesExistsValidator", StringComparison.OrdinalIgnoreCase)
            || body.Contains("has already been added", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts the caller-serialized series body to <c>PUT {baseUrl}/api/v3/series/{id}</c> — the v2 add-then-flip
    /// step that sets <c>monitored</c> (single-shot). v2 answers this with HTTP 202 (Accepted), a 2xx that
    /// classifies <see cref="WhisparrResultState.Ok"/> and returns the updated <see cref="WhisparrSeries"/>.
    /// The adapter owns the payload shape (mirrors <see cref="UpdateMovieAsync"/>).
    /// </summary>
    internal Task<WhisparrResult<WhisparrSeries>> UpdateSeriesAsync(string baseUrl, string apiKey, int id, string seriesJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"{baseUrl.TrimEnd('/')}/api/v3/series/{id}")
            {
                Content = new StringContent(seriesJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrSeries, token),
            ct);

    /// <summary>
    /// Posts a pre-serialized notification payload to <c>POST {baseUrl}/api/v3/notification</c> to register
    /// the Cove webhook connection. The caller (the adapter) owns the payload shape; this method is
    /// transport-only and single-shot — a non-idempotent POST is never blind-retried. On any 2xx JSON
    /// response this reports <see cref="WhisparrResultState.Ok"/>.
    /// </summary>
    internal Task<WhisparrResult<bool>> RegisterWebhookAsync(string baseUrl, string apiKey, string notificationJson, CancellationToken ct)
        => SendAsync<bool>(
            () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/v3/notification")
            {
                Content = new StringContent(notificationJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (_, _) => Task.FromResult(WhisparrResult<bool>.Ok(true)),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/studio?stashId={stashId}</c> — the studio identity lookup by
    /// StashDB id (query param; idempotent, bounded retry). Returns the (possibly empty)
    /// <see cref="WhisparrStudio"/> array; an empty array is a valid <see cref="WhisparrResultState.Ok"/>
    /// result meaning "not added to Whisparr yet". Transport-only.
    /// </summary>
    internal Task<WhisparrResult<WhisparrStudio[]>> GetStudioByStashIdAsync(string baseUrl, string apiKey, string stashId, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl.TrimEnd('/')}/api/v3/studio?stashId={Uri.EscapeDataString(stashId)}"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrStudioArray, token),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/performer/{stashId}</c> — the performer identity lookup by
    /// StashDB id (path param; idempotent, bounded retry). A 2xx returns <see cref="WhisparrResultState.Ok"/>
    /// with the performer; an HTTP 404 OR 500 is classified <see cref="WhisparrResultState.Absent"/>
    /// ("not added yet"), NOT Unreachable (the single wire nuance depends on). Every other non-2xx
    /// stays BadKey/NotWhisparr/Unreachable exactly as the shared send loop classifies.
    /// </summary>
    internal Task<WhisparrResult<WhisparrPerformer>> GetPerformerByStashIdAsync(string baseUrl, string apiKey, string stashId, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl.TrimEnd('/')}/api/v3/performer/{Uri.EscapeDataString(stashId)}"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrPerformer, token),
            ct,
            absentCodes: AbsentGetCodes);

    /// <summary>Reads <c>GET {baseUrl}/api/v3/studio</c> — every studio, so a card-badge batch resolves N studios in one call.</summary>
    internal Task<WhisparrResult<WhisparrStudio[]>> ListStudiosAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/studio"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrStudioArray, token),
            ct);

    /// <summary>Reads <c>GET {baseUrl}/api/v3/performer</c> — every performer, so a card-badge batch resolves N performers in one call.</summary>
    internal Task<WhisparrResult<WhisparrPerformer[]>> ListPerformersAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/performer"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrPerformerArray, token),
            ct);

    /// <summary>
    /// Posts the caller-serialized studio body to <c>POST {baseUrl}/api/v3/studio</c> (add;
    /// single-shot — a non-idempotent POST is never blind-retried). A 2xx returns the created
    /// <see cref="WhisparrStudio"/>; an HTTP 409 (already exists) is classified
    /// <see cref="WhisparrResultState.Conflict"/> for the caller to re-read, never a transport failure.
    /// The adapter owns the payload shape (mirrors <see cref="RegisterWebhookAsync"/>).
    /// </summary>
    internal Task<WhisparrResult<WhisparrStudio>> CreateStudioAsync(string baseUrl, string apiKey, string studioJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/v3/studio")
            {
                Content = new StringContent(studioJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrStudio, token),
            ct,
            conflictCodes: CreateConflictCodes);

    /// <summary>
    /// Posts the caller-serialized performer body to <c>POST {baseUrl}/api/v3/performer</c> (add;
    /// single-shot). A 2xx returns the created <see cref="WhisparrPerformer"/>; an HTTP 409 (already
    /// exists) is classified <see cref="WhisparrResultState.Conflict"/> for the caller to re-read, never a
    /// transport failure. The adapter owns the payload shape.
    /// </summary>
    internal Task<WhisparrResult<WhisparrPerformer>> CreatePerformerAsync(string baseUrl, string apiKey, string performerJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/v3/performer")
            {
                Content = new StringContent(performerJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrPerformer, token),
            ct,
            conflictCodes: CreateConflictCodes);

    /// <summary>
    /// Puts the caller-serialized studio body to <c>PUT {baseUrl}/api/v3/studio/{id}</c> — the
    /// add-then-flip step that sets <c>monitored</c> (single-shot). A 2xx returns the updated
    /// <see cref="WhisparrStudio"/>. The adapter owns the payload shape.
    /// </summary>
    internal Task<WhisparrResult<WhisparrStudio>> UpdateStudioAsync(string baseUrl, string apiKey, int id, string studioJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"{baseUrl.TrimEnd('/')}/api/v3/studio/{id}")
            {
                Content = new StringContent(studioJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrStudio, token),
            ct);

    /// <summary>
    /// Puts the caller-serialized performer body to <c>PUT {baseUrl}/api/v3/performer/{id}</c> — the
    /// add-then-flip step that sets <c>monitored</c> (single-shot). A 2xx returns the updated
    /// <see cref="WhisparrPerformer"/>. The adapter owns the payload shape.
    /// </summary>
    internal Task<WhisparrResult<WhisparrPerformer>> UpdatePerformerAsync(string baseUrl, string apiKey, int id, string performerJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"{baseUrl.TrimEnd('/')}/api/v3/performer/{id}")
            {
                Content = new StringContent(performerJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrPerformer, token),
            ct);

    /// <summary>
    /// Puts the caller-serialized <c>{ movieIds, monitored }</c> body to
    /// <c>PUT {baseUrl}/api/v3/movie/editor</c> — the v3 bulk movie(scene) monitor toggle (the "All scenes"
    /// scope lever). v3 answers HTTP 202; a 2xx classifies <see cref="WhisparrResultState.Ok"/>. Setting
    /// <c>monitored</c> only — the editor issues NO search, so it never grabs. The adapter owns the payload.
    /// </summary>
    internal Task<WhisparrResult<bool>> BulkMonitorMoviesAsync(string baseUrl, string apiKey, string editorJson, CancellationToken ct)
        => SendAsync<bool>(
            () => new HttpRequestMessage(HttpMethod.Put, $"{baseUrl.TrimEnd('/')}/api/v3/movie/editor")
            {
                Content = new StringContent(editorJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (_, _) => Task.FromResult(WhisparrResult<bool>.Ok(true)),
            ct);

    /// <summary>
    /// Puts the caller-serialized <c>{ episodeIds, monitored }</c> body to
    /// <c>PUT {baseUrl}/api/v3/episode/monitor</c> — the v2 (Sonarr) bulk episode(scene) monitor toggle (the
    /// "All scenes" scope lever for an already-added site). A 2xx classifies <see cref="WhisparrResultState.Ok"/>.
    /// Setting <c>monitored</c> only — issues NO episode search, so it never grabs. The adapter owns the payload.
    /// </summary>
    internal Task<WhisparrResult<bool>> MonitorEpisodesAsync(string baseUrl, string apiKey, string monitorJson, CancellationToken ct)
        => SendAsync<bool>(
            () => new HttpRequestMessage(HttpMethod.Put, $"{baseUrl.TrimEnd('/')}/api/v3/episode/monitor")
            {
                Content = new StringContent(monitorJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (_, _) => Task.FromResult(WhisparrResult<bool>.Ok(true)),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/tag</c> — the origin-tag lookup surface (idempotent, bounded
    /// retry). Returns the full <see cref="WhisparrTag"/> set for the caller to match by label. Transport-only.
    /// </summary>
    internal Task<WhisparrResult<WhisparrTag[]>> ListTagsAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/tag"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrTagArray, token),
            ct);

    /// <summary>
    /// Posts the caller-serialized <c>{ label }</c> body to <c>POST {baseUrl}/api/v3/tag</c> to create the
    /// origin tag when absent (single-shot). A 2xx returns the created <see cref="WhisparrTag"/>
    /// (its <c>id</c> is applied to a subsequent studio/performer add). The adapter owns the payload shape.
    /// </summary>
    internal Task<WhisparrResult<WhisparrTag>> CreateTagAsync(string baseUrl, string apiKey, string tagJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/v3/tag")
            {
                Content = new StringContent(tagJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrTag, token),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/exclusions</c> — the import-list exclusion set (idempotent,
    /// bounded retry). Returns the (possibly empty) <see cref="WhisparrExclusion"/> array; an empty array is
    /// a valid <see cref="WhisparrResultState.Ok"/> result meaning "no exclusions". Transport-only: the
    /// classify-not-throw guards (401/403 → BadKey, non-JSON → NotWhisparr) are inherited from
    /// <see cref="SendAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<WhisparrExclusion[]>> GetExclusionsAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/exclusions"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrExclusionArray, token),
            ct);

    /// <summary>
    /// Posts the caller-serialized exclusion body to <c>POST {baseUrl}/api/v3/exclusions</c> (add;
    /// single-shot — a non-idempotent POST is never blind-retried). A 2xx returns the created
    /// <see cref="WhisparrExclusion"/>; a duplicate is classified <see cref="WhisparrResultState.Conflict"/>
    /// for the caller to treat as an idempotent success — whether Whisparr signals it with an HTTP 409 or,
    /// like the movie add (Bug B), an HTTP 400 validation body naming an "already exists" exclusion
    /// validator. The adapter owns the payload shape (mirrors <see cref="CreateMovieAsync"/>).
    /// </summary>
    internal Task<WhisparrResult<WhisparrExclusion>> CreateExclusionAsync(string baseUrl, string apiKey, string exclusionJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/v3/exclusions")
            {
                Content = new StringContent(exclusionJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrExclusion, token),
            ct,
            conflictCodes: CreateConflictCodes,
            conflictBodyMatch: IsExclusionAlreadyExistsBody);

    // Whisparr can signal a duplicate exclusion ADD with an HTTP 400 validation body (the "already exists"
    // family) rather than a 409, exactly as the movie add does (Bug B). Recognise it by body so a re-exclude
    // resolves to the same non-error Conflict a 409 yields — the idempotency contract. A genuine
    // bad-body 400 (no exists marker) still falls through to Unreachable.
    private static bool IsExclusionAlreadyExistsBody(string body)
        => body.Contains("already", StringComparison.OrdinalIgnoreCase)
            || body.Contains("exists", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes an import-list exclusion by its Whisparr id at <c>DELETE {baseUrl}/api/v3/exclusions/{id}</c>
    /// (un-exclude; single-shot). Idempotent by design: a 2xx removes the row and a 404 means it was
    /// already gone — BOTH report <see cref="WhisparrResultState.Ok"/> (a DELETE's empty/non-JSON body
    /// bypasses the Content-Type guard). The caller resolves <paramref name="id"/> server-side by matching the
    /// scene's StashDB foreignId against the exclusion list — never a caller-supplied id.
    /// </summary>
    internal Task<WhisparrResult<bool>> DeleteExclusionAsync(string baseUrl, string apiKey, int id, CancellationToken ct)
        => SendAsync<bool>(
            () => new HttpRequestMessage(HttpMethod.Delete, $"{baseUrl.TrimEnd('/')}/api/v3/exclusions/{id}"),
            apiKey, PostMaxAttempts,
            (_, _) => Task.FromResult(WhisparrResult<bool>.Ok(true)),
            ct,
            bodilessSuccessCodes: DeleteIdempotentCodes);

    /// <summary>
    /// Posts the caller-serialized release body to <c>POST {baseUrl}/api/v3/release</c> — the
    /// interactive grab of one specific indexer release (single-shot; a grab is never blind-retried, so a
    /// transient fault can never double-grab). On any 2xx JSON response this reports
    /// <see cref="WhisparrResultState.Ok"/> — the caller cares only that the grab was accepted. This is the
    /// ONLY new grab-capable transport in v1.2 besides the existing <see cref="SendCommandAsync"/>
    /// <c>MoviesSearch</c>; it is a distinct single-shot verb, never fused into an add/exclusion path. The
    /// adapter owns the <c>{ guid, indexerId }</c> body (mirrors <see cref="SendCommandAsync"/>).
    /// </summary>
    internal Task<WhisparrResult<bool>> GrabReleaseAsync(string baseUrl, string apiKey, string releaseJson, CancellationToken ct)
        => SendAsync<bool>(
            () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/v3/release")
            {
                Content = new StringContent(releaseJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (_, _) => Task.FromResult(WhisparrResult<bool>.Ok(true)),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/release?movieId={movieId}</c> — the on-demand indexer release list for
    /// one movie (idempotent, bounded retry). Only the release count is consumed by the caller. NOT
    /// invoked eagerly — the scene panel calls it only on an explicit user expand, one movieId at a
    /// time. Transport-only: the classify-not-throw guards are inherited from <see cref="SendAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<WhisparrRelease[]>> GetReleasesAsync(string baseUrl, string apiKey, int movieId, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/release?movieId={movieId}"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrReleaseArray, token),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/movie?stashId={stashId}</c> — the per-scene identity lookup by StashDB
    /// id (query param; idempotent, bounded retry), the add/availability status primitive. Returns the
    /// (possibly empty) <see cref="WhisparrMovie"/> array; an empty array is a valid
    /// <see cref="WhisparrResultState.Ok"/> result meaning "not added to Whisparr yet", mirroring
    /// <see cref="GetStudioByStashIdAsync"/>. Transport-only: 401/403 → BadKey, a non-JSON body → NotWhisparr
    /// are inherited from <see cref="SendAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<WhisparrMovie[]>> GetMovieByStashIdAsync(string baseUrl, string apiKey, string stashId, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(
                HttpMethod.Get,
                $"{baseUrl.TrimEnd('/')}/api/v3/movie?stashId={Uri.EscapeDataString(stashId)}"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrMovieArray, token),
            ct);

    /// <summary>
    /// Posts the caller-serialized movie body to <c>POST {baseUrl}/api/v3/movie</c> (add /
    /// register; single-shot — a non-idempotent POST is never blind-retried). A 2xx returns the
    /// created <see cref="WhisparrMovie"/>; an HTTP 409 (already exists) is classified
    /// <see cref="WhisparrResultState.Conflict"/> for the caller to re-read — the idempotency
    /// spine — never a transport failure. The adapter owns the payload shape (foreignId/stashId, monitored,
    /// addOptions.searchForMovie, tags), mirrors <see cref="CreateStudioAsync"/>.
    /// </summary>
    /// <remarks>
    /// Whisparr Eros does NOT answer a duplicate movie add with 409 — it answers HTTP 400
    /// whose validation body names the <c>MovieExistsValidator</c> ("This movie has already been added"). So the
    /// idempotency spine must recognise that 400-with-exists body as the SAME non-error Conflict a 409 yields
    /// (via the <c>conflictBodyMatch</c> body predicate), or a re-add mis-classifies as Unreachable. A genuine
    /// bad-body 400 (no exists marker) still classifies as Unreachable.
    /// </remarks>
    internal Task<WhisparrResult<WhisparrMovie>> CreateMovieAsync(string baseUrl, string apiKey, string movieJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/v3/movie")
            {
                Content = new StringContent(movieJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrMovie, token),
            ct,
            conflictCodes: CreateConflictCodes,
            conflictBodyMatch: IsMovieAlreadyAddedBody);

    // Whisparr Eros signals a duplicate movie ADD with an HTTP 400 validation body naming the
    // MovieExistsValidator rather than a 409. Recognise it by body so the create path
    // treats an existing row as the same non-error Conflict a 409 yields — the idempotency contract.
    private static bool IsMovieAlreadyAddedBody(string body)
        => body.Contains("MovieExistsValidator", StringComparison.OrdinalIgnoreCase)
            || body.Contains("has already been added", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts the caller-serialized movie body to <c>PUT {baseUrl}/api/v3/movie/{id}</c> — the
    /// add-then-flip step that sets <c>monitored</c> (single-shot). A 2xx returns the updated
    /// <see cref="WhisparrMovie"/>. The adapter owns the payload shape (mirrors <see cref="UpdateStudioAsync"/>).
    /// </summary>
    internal Task<WhisparrResult<WhisparrMovie>> UpdateMovieAsync(string baseUrl, string apiKey, int id, string movieJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"{baseUrl.TrimEnd('/')}/api/v3/movie/{id}")
            {
                Content = new StringContent(movieJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrMovie, token),
            ct);

    /// <summary>
    /// Posts the caller-serialized command body to <c>POST {baseUrl}/api/v3/command</c> — the
    /// <c>MoviesSearch</c> trigger (single-shot; a search command is never blind-retried).
    /// On any 2xx JSON response this reports <see cref="WhisparrResultState.Ok"/> — the caller cares only
    /// that the command was accepted. The adapter owns the <c>{ name:"MoviesSearch", movieIds:[…] }</c> body;
    /// this method holds NO knowledge of which command runs (search is a distinct verb, never
    /// fused into an add/update path). Mirrors <see cref="RegisterWebhookAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<bool>> SendCommandAsync(string baseUrl, string apiKey, string commandJson, CancellationToken ct)
        => SendAsync<bool>(
            () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/v3/command")
            {
                Content = new StringContent(commandJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (_, _) => Task.FromResult(WhisparrResult<bool>.Ok(true)),
            ct);

    /// <summary>
    /// Posts the caller-serialized command body to <c>POST {baseUrl}/api/v3/command</c> and returns the
    /// QUEUED command's id (single-shot; a command is never blind-retried). Unlike
    /// <see cref="SendCommandAsync"/> — which reports only acceptance — this projects the 2xx body's
    /// <see cref="WhisparrCommand.Id"/>, so a caller can then poll <see cref="GetCommandAsync"/> and WAIT for
    /// the (asynchronous) command to land. The adapter owns the command body (e.g.
    /// <c>{ name:"RefreshStudios", studioIds:[…] }</c>); this method holds NO knowledge of which command runs.
    /// </summary>
    internal Task<WhisparrResult<int>> SendCommandForIdAsync(string baseUrl, string apiKey, string commandJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/api/v3/command")
            {
                Content = new StringContent(commandJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            async (resp, token) =>
            {
                var command = await DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrCommand, token);
                return command.IsOk
                    ? WhisparrResult<int>.Ok(command.Value!.Id)
                    : WhisparrResult<int>.PropagateFrom(command);
            },
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/command/{commandId}</c> — one queued command's status (idempotent;
    /// bounded retry), the read the wait loop polls until a refresh reports <c>completed</c>.
    /// <paramref name="commandId"/> is a Whisparr-supplied id (from <see cref="SendCommandForIdAsync"/>) used
    /// only to address that same command. Transport-only: the classify-not-throw guards are inherited from
    /// <see cref="SendAsync"/>; the adapter interprets the status string.
    /// </summary>
    internal Task<WhisparrResult<WhisparrCommand>> GetCommandAsync(string baseUrl, string apiKey, int commandId, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/command/{commandId}"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.WhisparrCommand, token),
            ct);

    /// <summary>
    /// Reads <c>GET {baseUrl}/api/v3/config/naming</c> — the naming config singleton (idempotent; bounded
    /// retry). The DTO carries the whole object verbatim so a subsequent <see cref="UpdateNamingConfigAsync"/>
    /// round-trips unknown fields. Transport-only: the classify-not-throw guards are inherited from
    /// <see cref="SendAsync"/>.
    /// </summary>
    internal Task<WhisparrResult<NamingConfig>> GetNamingConfigAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/config/naming"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.NamingConfig, token),
            ct);

    /// <summary>Reads <c>GET {baseUrl}/api/v3/config/mediamanagement</c> — the media-management config singleton (idempotent; bounded retry). Transport-only.</summary>
    internal Task<WhisparrResult<MediaManagementConfig>> GetMediaManagementConfigAsync(string baseUrl, string apiKey, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/v3/config/mediamanagement"),
            apiKey, GetMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.MediaManagementConfig, token),
            ct);

    /// <summary>
    /// Puts the caller-serialized naming config to <c>PUT {baseUrl}/api/v3/config/naming</c> (single-shot). The
    /// body MUST be the FULL singleton the caller read back and mutated — the config resource is a whole-object
    /// replace, so a partial body wipes the fields it omits. A 2xx returns the updated <see cref="NamingConfig"/>.
    /// The adapter owns the read-modify-write; this method is transport-only.
    /// </summary>
    internal Task<WhisparrResult<NamingConfig>> UpdateNamingConfigAsync(string baseUrl, string apiKey, string namingJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"{baseUrl.TrimEnd('/')}/api/v3/config/naming")
            {
                Content = new StringContent(namingJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.NamingConfig, token),
            ct);

    /// <summary>
    /// Puts the caller-serialized media-management config to <c>PUT {baseUrl}/api/v3/config/mediamanagement</c>
    /// (single-shot). Whole-object replace, same contract as <see cref="UpdateNamingConfigAsync"/>. A 2xx returns
    /// the updated <see cref="MediaManagementConfig"/>. Transport-only.
    /// </summary>
    internal Task<WhisparrResult<MediaManagementConfig>> UpdateMediaManagementConfigAsync(string baseUrl, string apiKey, string mediaJson, CancellationToken ct)
        => SendAsync(
            () => new HttpRequestMessage(HttpMethod.Put, $"{baseUrl.TrimEnd('/')}/api/v3/config/mediamanagement")
            {
                Content = new StringContent(mediaJson, Encoding.UTF8, "application/json"),
            },
            apiKey, PostMaxAttempts,
            (resp, token) => DeserializeAsync(resp, WhisparrJsonContext.Default.MediaManagementConfig, token),
            ct);

    /// <summary>
    /// The shared send loop: per-call timeout linked to the caller's token, the <c>X-Api-Key</c> header,
    /// the status (401/403 → BadKey) and <c>Content-Type</c> (non-JSON → NotWhisparr) guards BEFORE any
    /// deserialize, and a bounded retry on a transient transport fault (timeout / refused). A terminal
    /// classification (BadKey / NotWhisparr / a non-2xx JSON response) returns immediately without retry;
    /// only a thrown transport fault re-issues the request, and only while attempts remain.
    /// </summary>
    private async Task<WhisparrResult<T>> SendAsync<T>(
        Func<HttpRequestMessage> requestFactory,
        string apiKey,
        int maxAttempts,
        Func<HttpResponseMessage, CancellationToken, Task<WhisparrResult<T>>> onSuccess,
        CancellationToken ct,
        IReadOnlySet<HttpStatusCode>? absentCodes = null,
        IReadOnlySet<HttpStatusCode>? conflictCodes = null,
        Func<string, bool>? conflictBodyMatch = null,
        IReadOnlySet<HttpStatusCode>? bodilessSuccessCodes = null)
    {
        var last = WhisparrResult<T>.Unreachable("no response");
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(CallTimeout);

            try
            {
                using var req = requestFactory();

                // Guard the target URI at the transport edge (WR-02): a malformed absolute URL throws
                // UriFormatException from the request factory, and an empty/relative base URL yields a
                // relative RequestUri that http.SendAsync rejects with InvalidOperationException (no
                // BaseAddress). Reject both — plus any non-http(s) scheme — as a classified Unreachable
                // instead of letting the exception escape the classify-not-throw boundary as a 500. This
                // is deterministic, so it returns immediately rather than consuming a retry.
                if (req.RequestUri is not { IsAbsoluteUri: true } uri ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    return WhisparrResult<T>.Unreachable("invalid url");
                }

                req.Headers.TryAddWithoutValidation("X-Api-Key", apiKey);

                using var resp = await http.SendAsync(req, linked.Token);

                if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return WhisparrResult<T>.BadKey();
                }

                // A bodiless-success verb (the exclusion DELETE) declares the status codes it treats as
                // success — a 2xx AND a 404 (already-removed is the same idempotent success) — and
                // reports the caller's success WITHOUT the JSON Content-Type guard below, because a DELETE
                // legitimately answers with an empty / non-JSON body. Keyed on status so a bodiless 200 or a
                // 404 both land here rather than mis-classifying as NotWhisparr.
                if (bodilessSuccessCodes is not null && bodilessSuccessCodes.Contains(resp.StatusCode))
                {
                    return await onSuccess(resp, linked.Token);
                }

                // Caller-declared data outcomes are keyed on the status line (BEFORE the Content-Type guard
                // and the generic non-2xx→Unreachable): a 404/500 the performer GET declares absent
                // and a 409 a create POST declares conflict are typed results the adapter branches
                // on, never a transport fault. Keyed on status so a Whisparr problem+json body still lands here.
                if (absentCodes is not null && absentCodes.Contains(resp.StatusCode))
                {
                    return WhisparrResult<T>.Absent();
                }

                if (conflictCodes is not null && conflictCodes.Contains(resp.StatusCode))
                {
                    return WhisparrResult<T>.Conflict();
                }

                // Guard Content-Type BEFORE deserializing so a reverse-proxy HTML landing page / 502 becomes
                // a clean "not the Whisparr API" result rather than a confusing JSON-parse crash.
                var contentType = resp.Content.Headers.ContentType?.MediaType;
                if (contentType is not "application/json")
                {
                    return WhisparrResult<T>.NotWhisparr();
                }

                // A create POST can signal an already-existing row via a 400 validation body (Whisparr Eros's
                // MovieExistsValidator) rather than a 409. Inspect the body — a match is the
                // same non-error Conflict the caller re-reads; a non-matching 400 falls through to Unreachable.
                if (conflictBodyMatch is not null && resp.StatusCode == HttpStatusCode.BadRequest)
                {
                    var body = await resp.Content.ReadAsStringAsync(linked.Token);
                    if (conflictBodyMatch(body))
                    {
                        return WhisparrResult<T>.Conflict();
                    }
                }

                if (!resp.IsSuccessStatusCode)
                {
                    // Whisparr IS reachable here, so a non-2xx must not read as Unreachable: surface Whisparr's
                    // own message (the Content-Type guard above proved the body is JSON) as Rejected. A body
                    // with no message has no actionable reason, so it falls back to Unreachable.
                    var errorBody = await resp.Content.ReadAsStringAsync(linked.Token);
                    var message = TryExtractWhisparrError(errorBody);
                    return message is not null
                        ? WhisparrResult<T>.Rejected(message)
                        : WhisparrResult<T>.Unreachable($"HTTP {(int)resp.StatusCode}");
                }

                return await onSuccess(resp, linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The linked token fired (per-call timeout) rather than the caller cancelling.
                last = WhisparrResult<T>.Unreachable("timeout");
            }
            catch (HttpRequestException ex)
            {
                last = WhisparrResult<T>.Unreachable(ex.Message);
            }
            catch (Exception ex) when (ex is UriFormatException or InvalidOperationException)
            {
                // A malformed absolute URL (thrown while building the request) or a relative request URI
                // with no BaseAddress — deterministic, so classify and return without retrying.
                return WhisparrResult<T>.Unreachable("invalid url");
            }
        }

        return last;
    }

    // Whisparr reports an error as one of two JSON shapes: an object { "message", … } (e.g. a
    // grab/download-client fault) or an array of validation failures [{ "errorMessage", … }]. Pull the
    // human text from either; null for any other/empty body.
    private static string? TryExtractWhisparrError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.String)
            {
                var text = message.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }

            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 &&
                root[0].ValueKind == JsonValueKind.Object &&
                root[0].TryGetProperty("errorMessage", out var errorMessage) &&
                errorMessage.ValueKind == JsonValueKind.String)
            {
                var text = errorMessage.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
        }
        catch (JsonException)
        {
            // The body claimed JSON but did not parse — no message to surface; the caller falls back to
            // Unreachable(HTTP code).
        }

        return null;
    }

    private static async Task<WhisparrResult<T>> DeserializeAsync<T>(
        HttpResponseMessage resp, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        try
        {
            var value = await resp.Content.ReadFromJsonAsync(typeInfo, ct);
            return value is null ? WhisparrResult<T>.NotWhisparr() : WhisparrResult<T>.Ok(value);
        }
        catch (JsonException)
        {
            // The response claimed application/json but the body was not parseable — classify rather than
            // let the parse exception escape the transport boundary.
            return WhisparrResult<T>.NotWhisparr();
        }
    }
}
