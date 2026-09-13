using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
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
internal sealed class StashDbCatalogue
    : IProviderCatalogue,
        ISortsByTitle,
        ISortsByDate,
        ISortsByDuration,
        IListsPerformerFacet,
        IListsTagFacet,
        IListsSubStudioFacet,
        ISearchesTitles,
        ILooksUpByName
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

    /// <summary>Where StashDB shows a scene to a reader, which is not where it serves its API.</summary>
    internal const string SiteBase = "https://stashdb.org";

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

    /// <summary>The key a performer selection travels under, as the provider spells its own field.</summary>
    internal const string PerformerFacetKey = "performers";

    /// <summary>The key a tag selection travels under, as the provider spells its own field.</summary>
    internal const string TagFacetKey = "tags";

    /// <summary>The key a sub-studio selection travels under, as the provider spells its own field.</summary>
    internal const string SubStudioFacetKey = "studios";

    /// <summary>How many values a facet menu carries before it is offered as a type-ahead.</summary>
    /// <remarks>
    /// A large network's performer list runs to thousands and the whole tag set to more, so a menu
    /// read whole would grow with the provider rather than with the page.
    /// </remarks>
    internal const int FacetPageSize = 25;

    // The one catalogue query this type composes. Its field set is what the projector reads; a field
    // added here with no reader is a larger answer bought for nothing.
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

    private const string StudioByNameQuery = """
        query FindStudioByName($name: String!) {
          findStudio(name: $name) { id name }
        }
        """;

    // findTag answers null for a word that is an alias of a canonical tag, and reporting no
    // catalogue for a very common tag would be a false negative.
    private const string TagByNameQuery = """
        query FindTagOrAliasByName($name: String!) {
          findTagOrAlias(name: $name) { id name }
        }
        """;

    // There is no exact find-by-name for a performer, so the exact filter is this product's own.
    private const string PerformersQuery = """
        query QueryPerformers($input: PerformerQueryInput!) {
          queryPerformers(input: $input) { count performers { id name } }
        }
        """;

    private const string SubStudiosQuery = """
        query QuerySubStudios($input: StudioQueryInput!) {
          queryStudios(input: $input) { count studios { id name } }
        }
        """;

    private const string TagsQuery = """
        query QueryTags($input: TagQueryInput!) {
          queryTags(input: $input) { count tags { id name } }
        }
        """;

    // Both are NON_NULL on the provider's own input, so both are always sent.
    private const string SortByDate = "DATE";
    private const string DescendingFirst = "DESC";
    private const string AscendingFirst = "ASC";

    private const string NewestFirst = $"{SortByDate}:{DescendingFirst}";

    /// <inheritdoc/>
    /// <remarks>
    /// The provider's own ordering and direction, joined into one opaque value because its input
    /// carries them as two non-null fields. Nothing outside this type reads either half.
    /// </remarks>
    public IReadOnlyList<ProviderSortOption> Sorts { get; } =
    [
        new(NewestFirst, "Newest first"),
        new($"{SortByDate}:{AscendingFirst}", "Oldest first"),
        new($"TITLE:{AscendingFirst}", "Title A to Z"),
        new($"TITLE:{DescendingFirst}", "Title Z to A"),
        new($"DURATION:{DescendingFirst}", "Longest first"),
        new($"DURATION:{AscendingFirst}", "Shortest first"),
    ];

    /// <inheritdoc/>
    public string DefaultSort => NewestFirst;

    public ProviderCapabilitySet Capabilities { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// The site address rather than the GraphQL one the catalogue is read from. Measured: a real
    /// identifier under this path redirected to the sign-in page carrying the same path back, so
    /// the route resolves and only the sign-in was missing.
    /// </remarks>
    public string? SceneAddress(string providerSceneId)
        => string.IsNullOrWhiteSpace(providerSceneId)
            ? null
            : $"{SiteBase}/scenes/{Uri.EscapeDataString(providerSceneId)}";

    public async Task<ProviderCatalogueAnswer> ReadPageAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var answered = await AskAsync(PageQuery, Scope(request), ct).ConfigureAwait(false);
        if (answered is null || !answered.Value.TryGetProperty("queryScenes", out var result))
        {
            return ProviderCatalogueAnswer.NotReached;
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

        return ProviderCatalogueAnswer.Answered(
            new ProviderCataloguePage(
                scenes,
                size,
                SizeIsLowerBound: false,
                Math.Max(lastPage, 1),
                rangeFrom,
                Math.Min(rangeFrom + request.PerPage - 1, Math.Max(size, rangeFrom))));
    }

    public async Task<int?> ReadCatalogueSizeAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var answered = await AskAsync(CountQuery, Scope(request), ct).ConfigureAwait(false);
        return answered is null || !answered.Value.TryGetProperty("queryScenes", out var result)
            ? null
            : Count(result);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// None, and no request is sent to establish it. This provider names a scene by its uuid and by
    /// nothing else, so there is no number of its own to resolve to. It holds no
    /// <see cref="IResolvesNumericSceneId"/> role either, so a caller that asks by role is refused
    /// before reaching this null; a caller that reaches it anyway counts the scene unnumbered.
    /// </remarks>
    public Task<int?> ResolveNumericSceneIdAsync(string providerSceneId, CancellationToken ct)
        => Task.FromResult<int?>(null);

    /// <inheritdoc/>
    public Task<ProviderSiteNumber> ResolveNumericSiteIdAsync(
        string providerSiteId, CancellationToken ct)
        => Task.FromResult(ProviderSiteNumber.NotReached);

    /// <inheritdoc/>
    /// <remarks>
    /// A studio and a tag are found exactly by the provider itself. A performer has no exact
    /// find-by-name, so its search result is filtered here and several exact matches answer as
    /// ambiguity rather than as whichever the provider happened to return first.
    /// </remarks>
    public async Task<ProviderIdentityLookup> LookUpByNameAsync(
        WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        foreach (var candidate in Candidates(name, aliases))
        {
            var found = kind switch
            {
                WhisparrEntityKind.Performer => await PerformerByNameAsync(candidate, ct)
                    .ConfigureAwait(false),
                WhisparrEntityKind.Tag => await FoundByNameAsync(
                        TagByNameQuery, "findTagOrAlias", candidate, ct)
                    .ConfigureAwait(false),
                _ => await FoundByNameAsync(StudioByNameQuery, "findStudio", candidate, ct)
                    .ConfigureAwait(false),
            };

            // A candidate that did not reach the provider ends the walk. Asking under the next
            // alias would send again into the same failure and could answer an absence for it.
            if (!found.WasReached || found.IsAmbiguous || found.ProviderEntityId is not null)
            {
                return found;
            }
        }

        return ProviderIdentityLookup.Unmatched;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// A studio's own performers and sub-studios are listable and a tag's are not, so a tag page is
    /// offered no menu at all rather than one that would narrow to itself.
    /// </remarks>
    public async Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
        WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
    {
        var menus = new List<ProviderFacetMenu>();

        foreach (var key in MenuOrder)
        {
            if (FacetQueryFor(kind, key, providerEntityId) is not { } facet)
            {
                continue;
            }

            if (await MenuAsync(facet, ct).ConfigureAwait(false) is { } menu)
            {
                menus.Add(menu);
            }
        }

        return menus;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The same queries the menus are filled from, under the provider's own <c>name</c> criterion,
    /// which it matches on a substring. Its <c>alias</c> criterion is not used: it was measured
    /// answering the whole set, so it is dropped rather than honoured, and a filter dropped in
    /// silence is worse than one refused.
    /// </remarks>
    public async Task<ProviderFacetSearch> SearchFacetValuesAsync(
        WhisparrEntityKind kind,
        string providerEntityId,
        string facetKey,
        string fragment,
        CancellationToken ct)
    {
        // A key that is no menu on this entity's page narrows a set the reader is not looking at, so
        // it is answered as unsearchable rather than asked for.
        if (FacetQueryFor(kind, facetKey, providerEntityId) is not { } facet)
        {
            return ProviderFacetSearch.NotSearchable;
        }

        facet.Input["name"] = fragment;

        var read = await ValuesAsync(facet, ct).ConfigureAwait(false);
        return read is not { } answered
            ? ProviderFacetSearch.NotReached
            : ProviderFacetSearch.Matched(answered.Values, answered.Reported);
    }

    /// <summary>The scope one catalogue request narrows the provider's scenes to.</summary>
    /// <remarks>
    /// A studio reads its own scenes, or itself and every descendant where the surface says the
    /// host's sub-studio toggle is on. A chosen sub-studio replaces that scope rather than joining
    /// it, so the selection narrows what is read instead of widening it.
    /// </remarks>
    internal static JsonObject ScopeFor(ProviderCatalogueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (sort, direction) = Ordering(request.Sort);
        var scope = new JsonObject
        {
            ["page"] = request.Page,
            ["per_page"] = request.PerPage,
            ["sort"] = sort,
            ["direction"] = direction,
        };

        if (request.TitleSearch is { Length: > 0 } search)
        {
            scope["title"] = search;
        }

        var entityKey = ScopeKeyFor(request.Kind);
        var chosenSubStudio = Chosen(request, SubStudioFacetKey);

        if (request.Kind == WhisparrEntityKind.Studio && chosenSubStudio is { Length: > 0 } child)
        {
            scope[SubStudioFacetKey] = Criterion(child);
        }
        else if (request.Kind == WhisparrEntityKind.Studio && IncludesSubStudios(request))
        {
            scope["parentStudio"] = request.ProviderEntityId;
        }
        else
        {
            scope[entityKey] = Criterion(request.ProviderEntityId);
        }

        foreach (var key in (string[])[PerformerFacetKey, TagFacetKey])
        {
            if (!string.Equals(key, entityKey, StringComparison.Ordinal)
                && Chosen(request, key) is { Length: > 0 } value)
            {
                scope[key] = Criterion(value);
            }
        }

        return scope;
    }

    /// <summary>One facet's query, its scope, and how the menu it fills reads.</summary>
    private sealed record FacetQuery(
        string Query,
        string Member,
        string Collection,
        string Key,
        string Label,
        JsonObject Input);

    /// <summary>The menus a page offers, in the order they are drawn.</summary>
    private static readonly string[] MenuOrder =
        [PerformerFacetKey, SubStudioFacetKey, TagFacetKey];

    /// <summary>
    /// The query the facet <paramref name="facetKey"/> names is read through, or null where this
    /// entity kind is offered no such menu.
    /// </summary>
    /// <remarks>
    /// The one table both the menu fill and the value search read, so the two cannot come to offer
    /// different facets. A studio's own performers and sub-studios are listable and a tag's are not,
    /// and a tag menu on a tag page would narrow a tag to itself.
    /// </remarks>
    private static FacetQuery? FacetQueryFor(
        WhisparrEntityKind kind, string facetKey, string providerEntityId)
        => facetKey switch
        {
            PerformerFacetKey when kind == WhisparrEntityKind.Studio => new(
                PerformersQuery,
                "queryPerformers",
                "performers",
                PerformerFacetKey,
                "Performers",
                new JsonObject { ["studio_id"] = providerEntityId }),
            SubStudioFacetKey when kind == WhisparrEntityKind.Studio => new(
                SubStudiosQuery,
                "queryStudios",
                "studios",
                SubStudioFacetKey,
                "Sub-studios",
                new JsonObject { ["parent"] = Criterion(providerEntityId) }),
            TagFacetKey when kind != WhisparrEntityKind.Tag => new(
                TagsQuery, "queryTags", "tags", TagFacetKey, "Tags", []),
            _ => null,
        };

    private static JsonObject Scope(ProviderCatalogueRequest request)
        => new() { ["input"] = ScopeFor(request) };

    private static JsonObject Criterion(string value)
        => new() { ["value"] = new JsonArray(value), ["modifier"] = "INCLUDES" };

    private static string? Chosen(ProviderCatalogueRequest request, string key)
        => request.Filters.TryGetValue(key, out var value) ? value : null;

    // The provider issued the whole option, so a value this type did not compose is not read into
    // halves it might not carry.
    private static (string Sort, string Direction) Ordering(string? option)
    {
        if (option is not { Length: > 0 })
        {
            return (SortByDate, DescendingFirst);
        }

        var split = option.Split(':');
        return split.Length == 2 && split[0].Length > 0 && split[1].Length > 0
            ? (split[0], split[1])
            : (SortByDate, DescendingFirst);
    }

    private static IEnumerable<string> Candidates(string name, IReadOnlyList<string> aliases)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            yield return name;
        }

        foreach (var alias in aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                yield return alias;
            }
        }
    }

    private static bool IncludesSubStudios(ProviderCatalogueRequest request)
        => request.Filters.TryGetValue(IncludeSubStudiosKey, out var value)
            && bool.TryParse(value, out var included)
            && included;

    private static string ScopeKeyFor(WhisparrEntityKind kind)
        => kind switch
        {
            WhisparrEntityKind.Performer => PerformerFacetKey,
            WhisparrEntityKind.Tag => TagFacetKey,
            _ => SubStudioFacetKey,
        };

    private static int Count(JsonElement result)
        => result.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number
            ? count.GetInt32()
            : 0;

    private static string? Text(JsonElement row, string property)
        => row.ValueKind == JsonValueKind.Object
            && row.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private async Task<ProviderIdentityLookup> FoundByNameAsync(
        string query, string member, string name, CancellationToken ct)
    {
        var answered = await AskAsync(query, new JsonObject { ["name"] = name }, ct)
            .ConfigureAwait(false);
        if (answered is null)
        {
            return ProviderIdentityLookup.NotReached;
        }

        return answered.Value.TryGetProperty(member, out var found)
            && found.ValueKind == JsonValueKind.Object
            && Text(found, "id") is { Length: > 0 } id
                ? ProviderIdentityLookup.Matched(id)
                : ProviderIdentityLookup.Unmatched;
    }

    private async Task<ProviderIdentityLookup> PerformerByNameAsync(
        string name, CancellationToken ct)
    {
        var variables = new JsonObject
        {
            ["input"] = new JsonObject
            {
                ["name"] = name,
                ["page"] = 1,
                ["per_page"] = FacetPageSize,
            },
        };

        var answered = await AskAsync(PerformersQuery, variables, ct).ConfigureAwait(false);
        if (answered is null)
        {
            return ProviderIdentityLookup.NotReached;
        }

        if (!answered.Value.TryGetProperty("queryPerformers", out var result)
            || !result.TryGetProperty("performers", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return ProviderIdentityLookup.Unmatched;
        }

        var matched = new List<string>();
        foreach (var row in rows.EnumerateArray())
        {
            if (string.Equals(Text(row, "name"), name, StringComparison.OrdinalIgnoreCase)
                && Text(row, "id") is { Length: > 0 } id)
            {
                matched.Add(id);
            }
        }

        return matched.Count switch
        {
            1 => ProviderIdentityLookup.Matched(matched[0]),
            > 1 => ProviderIdentityLookup.Ambiguous,
            _ => ProviderIdentityLookup.Unmatched,
        };
    }

    private async Task<ProviderFacetMenu?> MenuAsync(FacetQuery facet, CancellationToken ct)
    {
        var read = await ValuesAsync(facet, ct).ConfigureAwait(false);

        // A menu the provider filled with nothing is absent rather than empty, which a search's own
        // empty answer is not: that one is a measurement of what matches.
        return read is not { } answered || answered.Values.Count == 0
            ? null
            : new ProviderFacetMenu(
                facet.Key, facet.Label, answered.Values, answered.Reported);
    }

    // Null where no whole answer arrived. One page of values, so a menu read whole cannot grow with
    // the provider's own list.
    private async Task<(IReadOnlyList<ProviderFacetValue> Values, int Reported)?> ValuesAsync(
        FacetQuery facet, CancellationToken ct)
    {
        facet.Input["page"] = 1;
        facet.Input["per_page"] = FacetPageSize;

        var answered = await AskAsync(
                facet.Query, new JsonObject { ["input"] = facet.Input }, ct)
            .ConfigureAwait(false);
        if (answered is null
            || !answered.Value.TryGetProperty(facet.Member, out var result)
            || !result.TryGetProperty(facet.Collection, out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<ProviderFacetValue>();
        foreach (var row in rows.EnumerateArray())
        {
            if (Text(row, "id") is { Length: > 0 } id && Text(row, "name") is { Length: > 0 } name)
            {
                values.Add(new ProviderFacetValue(id, name));
            }
        }

        // An absent count member reads as zero, which is no measurement of how many there are.
        var reported = Count(result);
        return (values, reported == 0 ? values.Count : reported);
    }

    // Null where no catalogue arrived: no whole answer, a status that is not a success, or a body
    // carrying the provider's own errors. The provider answers an authentication failure with 200,
    // so the body is what decides. An answer the provider stated ends the attempts.
    private async Task<JsonElement?> AskAsync(
        string query, JsonObject variables, CancellationToken ct)
    {
        var stored = await _options.LoadAsync(ct).ConfigureAwait(false);
        var resolved = _endpoints.Resolve(
            WhisparrGeneration.V3, stored.MetadataProviderEndpoints);
        if (resolved is null)
        {
            return null;
        }

        var body = new JsonObject { ["query"] = query, ["variables"] = variables };

        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var sent = await TrySendAsync(resolved, body, ct).ConfigureAwait(false);
            if (sent.Body is { } carried)
            {
                return carried;
            }

            if (sent.WasDefinite)
            {
                return null;
            }
        }

        return null;
    }

    private async Task<ProviderSend> TrySendAsync(
        ResolvedProvider resolved, JsonObject body, CancellationToken ct)
    {
        if (!await _pacer.WaitForTurnAsync(resolved.MaxRequestsPerMinute, ct).ConfigureAwait(false))
        {
            return ProviderSend.Nothing;
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

            var answered = await ProviderResponseBound.ReadAsync(response.Content, attempt.Token)
                .ConfigureAwait(false);
            if (answered is null)
            {
                WhisparrSyncLog.ProviderAnswerBeyondReadBound(
                    _log, ProviderName, WhisparrClient.MaxResponseBytes);
                return ProviderSend.Nothing;
            }

            return response.IsSuccessStatusCode
                ? Parse(answered)
                : ProviderSend.From(response.StatusCode);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            return ProviderSend.Nothing;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ProviderSend.Nothing;
        }
    }

    private ProviderSend Parse(string answered)
    {
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(answered);
        }
        catch (JsonException)
        {
            return ProviderSend.Nothing;
        }

        using (parsed)
        {
            if (parsed.RootElement.TryGetProperty("errors", out var errors)
                && errors.ValueKind == JsonValueKind.Array
                && errors.GetArrayLength() > 0)
            {
                WhisparrSyncLog.ProviderRefusedTheQuery(_log, ProviderName);
                return ProviderSend.Refused;
            }

            return parsed.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                    ? ProviderSend.Carrying(data.Clone())
                    : ProviderSend.Nothing;
        }
    }
}
