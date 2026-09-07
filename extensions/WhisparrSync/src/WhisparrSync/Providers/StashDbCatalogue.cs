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

    /// <inheritdoc/>
    /// <remarks>
    /// The provider's own ordering and direction, joined into one opaque value because its input
    /// carries them as two non-null fields. Nothing outside this type reads either half.
    /// </remarks>
    public IReadOnlyList<ProviderSortOption> Sorts { get; } =
    [
        new($"{SortByDate}:{DescendingFirst}", "Newest first"),
        new($"{SortByDate}:{AscendingFirst}", "Oldest first"),
        new($"TITLE:{AscendingFirst}", "Title A to Z"),
        new($"TITLE:{DescendingFirst}", "Title Z to A"),
        new($"DURATION:{DescendingFirst}", "Longest first"),
        new($"DURATION:{AscendingFirst}", "Shortest first"),
    ];

    public ProviderCapabilitySet Capabilities { get; }

    public async Task<ProviderCataloguePage> ReadPageAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var answered = await AskAsync(PageQuery, Scope(request), ct).ConfigureAwait(false);
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

        var answered = await AskAsync(CountQuery, Scope(request), ct).ConfigureAwait(false);
        return answered is null || !answered.Value.TryGetProperty("queryScenes", out var result)
            ? null
            : Count(result);
    }

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

            if (found.IsAmbiguous || found.ProviderEntityId is not null)
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

        if (kind == WhisparrEntityKind.Studio)
        {
            var performers = await MenuAsync(
                    PerformersQuery,
                    "queryPerformers",
                    "performers",
                    new JsonObject { ["studio_id"] = providerEntityId },
                    PerformerFacetKey,
                    "Performers",
                    ct)
                .ConfigureAwait(false);
            if (performers is not null)
            {
                menus.Add(performers);
            }

            var subStudios = await MenuAsync(
                    SubStudiosQuery,
                    "queryStudios",
                    "studios",
                    new JsonObject { ["parent"] = Criterion(providerEntityId) },
                    SubStudioFacetKey,
                    "Sub-studios",
                    ct)
                .ConfigureAwait(false);
            if (subStudios is not null)
            {
                menus.Add(subStudios);
            }
        }

        if (kind != WhisparrEntityKind.Tag)
        {
            var tags = await MenuAsync(
                    TagsQuery, "queryTags", "tags", [], TagFacetKey, "Tags", ct)
                .ConfigureAwait(false);
            if (tags is not null)
            {
                menus.Add(tags);
            }
        }

        return menus;
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

    // A read that answered nothing. Zero scenes and a zero size, which the surface states as a
    // refusal rather than as an empty catalogue.
    private static ProviderCataloguePage EmptyPage { get; } =
        new([], 0, SizeIsLowerBound: false, 1, 1, 0);

    private async Task<ProviderIdentityLookup> FoundByNameAsync(
        string query, string member, string name, CancellationToken ct)
    {
        var answered = await AskAsync(query, new JsonObject { ["name"] = name }, ct)
            .ConfigureAwait(false);

        return answered is not null
            && answered.Value.TryGetProperty(member, out var found)
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
        if (answered is null
            || !answered.Value.TryGetProperty("queryPerformers", out var result)
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

    private async Task<ProviderFacetMenu?> MenuAsync(
        string query,
        string member,
        string collection,
        JsonObject scope,
        string key,
        string label,
        CancellationToken ct)
    {
        scope["page"] = 1;
        scope["per_page"] = FacetPageSize;

        var answered = await AskAsync(query, new JsonObject { ["input"] = scope }, ct)
            .ConfigureAwait(false);
        if (answered is null
            || !answered.Value.TryGetProperty(member, out var result)
            || !result.TryGetProperty(collection, out var rows)
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

        return values.Count == 0
            ? null
            : new ProviderFacetMenu(key, label, values, Count(result) > values.Count);
    }

    // Null where no catalogue arrived: no whole answer, a status that is not a success, or a body
    // carrying the provider's own errors. The provider answers an authentication failure with 200,
    // so the body is what decides.
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

            var answered = await ProviderResponseBound.ReadAsync(response.Content, attempt.Token)
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
}
