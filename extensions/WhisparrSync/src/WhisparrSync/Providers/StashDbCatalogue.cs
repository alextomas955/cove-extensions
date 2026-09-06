using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Providers;

/// <summary>Reads a catalogue from StashDB.</summary>
/// <remarks>
/// Narrow in the same sense this product's instance client is: every request is this one POST to the
/// configured endpoint, and no member takes a path, verb or query string from a caller.
/// <para>
/// StashDB answers an authentication failure with 200 and an <c>errors</c> member, so a status alone
/// does not say whether a body carries a catalogue.
/// </para>
/// </remarks>
internal sealed class StashDbCatalogue : IProviderCatalogue, ISortsByDate
{
    private readonly HttpClient _http;
    private readonly ProviderEndpointPort _endpoints;
    private readonly OptionsStore _options;
    private readonly ProviderPacer _pacer;
    private readonly ILogger _log;

    public StashDbCatalogue(
        HttpClient http,
        ProviderEndpointPort endpoints,
        OptionsStore options,
        ProviderPacer pacer,
        ILogger log)
    {
        _http = http;
        _endpoints = endpoints;
        _options = options;
        _pacer = pacer;
        _log = log;
        Capabilities = ProviderCapabilities.ForStashDb(this);
    }

    /// <summary>The header StashDB authenticates with.</summary>
    /// <remarks>
    /// Not the bearer scheme. A bearer header is accepted, answered with 200 and refused inside the
    /// body, so a client sending one reads as reaching a catalogue that is always empty.
    /// </remarks>
    internal const string ApiKeyHeader = "ApiKey";

    /// <summary>The provider this catalogue names itself as.</summary>
    internal const string ProviderName = "StashDB";

    /// <summary>
    /// Whether a studio's sub-studios are counted as part of it, keyed as the surface sends it.
    /// </summary>
    /// <remarks>
    /// Mirrors the host studio page's own toggle, which the surface reads from the address rather
    /// than owning. <c>parentStudio</c> matches a studio or any descendant of it, so a studio with no
    /// children answers its own scenes under either spelling.
    /// </remarks>
    internal const string IncludeSubStudiosKey = "includeSubStudios";

    // The one query this type composes. Its field set is what the projector reads; a field added
    // here with no reader is a larger answer bought for nothing.
    private const string PageQuery = """
        query MissingPage($input: SceneQueryInput!) {
          queryScenes(input: $input) {
            count
            scenes {
              id
              title
              details
              release_date
              studio { id name }
              performers { performer { id name images { url } } }
              tags { id name }
              images { url }
            }
          }
        }
        """;

    private const string CountQuery = """
        query MissingCount($input: SceneQueryInput!) {
          queryScenes(input: $input) { count }
        }
        """;

    // Both are NON_NULL on the provider's own input, so both are always sent.
    private const string SortByDate = "DATE";
    private const string DescendingFirst = "DESC";

    public IReadOnlyList<ProviderSortOption> Sorts { get; } =
        [new ProviderSortOption(SortByDate, "Newest first")];

    public ProviderCapabilitySet Capabilities { get; }

    public async Task<ProviderCataloguePage> ReadPageAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var answered = await AskAsync(PageQuery, request, ct).ConfigureAwait(false);
        if (answered is null || !answered.Value.TryGetProperty("queryScenes", out var result))
        {
            return EmptyPage;
        }

        var scenes = new List<ProviderScene>();
        if (result.TryGetProperty("scenes", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (ProviderSceneProjector.Project(row) is { } scene)
                {
                    scenes.Add(scene);
                }
            }
        }

        var size = Count(result);

        // The provider's count is exact at every page size and a short page really is the end, so the
        // last page is derived from it rather than probed for.
        var lastPage = size <= 0 ? 1 : (int)Math.Ceiling(size / (double)request.PerPage);
        var rangeFrom = ((request.Page - 1) * request.PerPage) + 1;

        return new ProviderCataloguePage(
            scenes,
            size,
            SizeIsLowerBound: false,
            Math.Max(lastPage, 1),
            rangeFrom,
            Math.Min(rangeFrom + request.PerPage - 1, Math.Max(size, rangeFrom)));
    }

    public async Task<int?> ReadCatalogueSizeAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var answered = await AskAsync(CountQuery, request, ct).ConfigureAwait(false);
        return answered is null || !answered.Value.TryGetProperty("queryScenes", out var result)
            ? null
            : Count(result);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Answers unmatched while the provider's own name lookup is unimplemented. An entity carrying no
    /// stored identifier therefore reads as having no catalogue, which narrows what the surface can
    /// answer for and claims nothing untrue about the provider.
    /// </remarks>
    public Task<ProviderIdentityLookup> LookUpByNameAsync(
        WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct)
        => Task.FromResult(ProviderIdentityLookup.Unmatched);

    /// <inheritdoc/>
    /// <remarks>
    /// Answers no menu while the provider's own facet reads are unimplemented. A menu the provider
    /// has not been asked for is absent rather than present and empty.
    /// </remarks>
    public Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
        WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ProviderFacetMenu>>([]);

    /// <summary>The scope one catalogue request narrows the provider's scenes to.</summary>
    /// <remarks>
    /// A studio reads its own scenes, or itself and every descendant where the surface says the
    /// host's sub-studio toggle is on.
    /// </remarks>
    internal static JsonObject ScopeFor(ProviderCatalogueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scope = new JsonObject
        {
            ["page"] = request.Page,
            ["per_page"] = request.PerPage,
            ["sort"] = request.Sort is { Length: > 0 } sort ? sort : SortByDate,
            ["direction"] = DescendingFirst,
        };

        if (request.TitleSearch is { Length: > 0 } search)
        {
            scope["title"] = search;
        }

        if (request.Kind == WhisparrEntityKind.Studio && IncludesSubStudios(request))
        {
            scope["parentStudio"] = request.ProviderEntityId;
        }
        else
        {
            scope[ScopeKeyFor(request.Kind)] = new JsonObject
            {
                ["value"] = new JsonArray(request.ProviderEntityId),
                ["modifier"] = "INCLUDES",
            };
        }

        return scope;
    }

    private static bool IncludesSubStudios(ProviderCatalogueRequest request)
        => request.Filters.TryGetValue(IncludeSubStudiosKey, out var value)
            && bool.TryParse(value, out var included)
            && included;

    private static string ScopeKeyFor(WhisparrEntityKind kind)
        => kind switch
        {
            WhisparrEntityKind.Performer => "performers",
            WhisparrEntityKind.Tag => "tags",
            _ => "studios",
        };

    private static int Count(JsonElement result)
        => result.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number
            ? count.GetInt32()
            : 0;

    // A read that answered nothing. Zero scenes and a zero size, which the surface states as a
    // refusal rather than as an empty catalogue.
    private static ProviderCataloguePage EmptyPage { get; } =
        new([], 0, SizeIsLowerBound: false, 1, 1, 0);

    // Null where no catalogue arrived: no whole answer, a status that is not a success, or a body
    // carrying the provider's own errors. The provider answers an authentication failure with 200,
    // so the body is what decides.
    private async Task<JsonElement?> AskAsync(
        string query, ProviderCatalogueRequest request, CancellationToken ct)
    {
        var stored = await _options.LoadAsync(ct).ConfigureAwait(false);
        var resolved = _endpoints.Resolve(
            WhisparrGeneration.V3, stored.MetadataProviderEndpoints);
        if (resolved is null)
        {
            return null;
        }

        var body = new JsonObject
        {
            ["query"] = query,
            ["variables"] = new JsonObject { ["input"] = ScopeFor(request) },
        };

        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            if (await TrySendAsync(resolved, body, ct).ConfigureAwait(false) is { } retried)
            {
                return retried;
            }
        }

        return await TrySendAsync(resolved, body, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement?> TrySendAsync(
        ResolvedProvider resolved, JsonObject body, CancellationToken ct)
    {
        if (!await _pacer.WaitForTurnAsync(resolved.MaxRequestsPerMinute, ct).ConfigureAwait(false))
        {
            return null;
        }

        // The address is the configured endpoint read per request, so a source the host is
        // reconfigured to reaches this without the container being rebuilt.
        using var request = new HttpRequestMessage(HttpMethod.Post, resolved.IdentityEndpoint)
        {
            Content = new StringContent(
                body.ToJsonString(), Encoding.UTF8, MediaTypeNames.Application.Json),
        };
        request.Headers.Add(ApiKeyHeader, resolved.ApiKey);

        // The whole attempt is bounded here, headers and body alike. The client's own timeout stops
        // at the headers once the body is asked for separately.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_http.Timeout);

        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                .ConfigureAwait(false);

            var answered = await ReadWithinBoundAsync(response.Content, attempt.Token)
                .ConfigureAwait(false);
            if (answered is null)
            {
                WhisparrSyncLog.ProviderAnswerBeyondReadBound(
                    _log, ProviderName, WhisparrClient.MaxResponseBytes);
                return null;
            }

            return response.IsSuccessStatusCode ? Parse(answered) : null;
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private JsonElement? Parse(string answered)
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(answered);
        }
        catch (JsonException)
        {
            return null;
        }

        using (parsed)
        {
            if (parsed.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0)
            {
                WhisparrSyncLog.ProviderRefusedTheQuery(_log, ProviderName);
                return null;
            }

            return parsed.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                    ? data.Clone()
                    : null;
        }
    }

    // Null when the answer is past the bound. A truncated body parses as a valid short page, so the
    // read refuses rather than returning what it managed to hold.
    private static async Task<string?> ReadWithinBoundAsync(
        HttpContent content, CancellationToken ct)
    {
        const int ChunkBytes = 64 * 1024;
        var ceiling = WhisparrClient.MaxResponseBytes + 1;

        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffered = new MemoryStream();
            var chunk = new byte[ChunkBytes];

            while (buffered.Length < ceiling)
            {
                var wanted = (int)Math.Min(chunk.Length, ceiling - buffered.Length);
                var read = await stream.ReadAsync(chunk.AsMemory(0, wanted), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    return Encoding.UTF8.GetString(buffered.GetBuffer(), 0, (int)buffered.Length);
                }

                await buffered.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            return null;
        }
    }
}
