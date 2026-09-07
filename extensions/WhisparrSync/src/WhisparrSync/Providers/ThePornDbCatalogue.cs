using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Providers;

/// <summary>Reads a catalogue from ThePornDB.</summary>
/// <remarks>
/// REST rather than the GraphQL address Cove is configured with: that surface answers a scene query
/// with the requested page size as its count and a null scene list, so a caller reading it would
/// state a page size as a catalogue's size and never see an error.
/// <para>
/// Narrow in the same sense this product's instance client is: every request is a GET on one of the
/// routes named here, and no member takes a path, verb or query key from a caller.
/// </para>
/// </remarks>
internal sealed class ThePornDbCatalogue
    : IProviderCatalogue,
        ISortsByDate,
        ISortsByDuration,
        IFiltersByYear,
        IListsTagFacet,
        ISearchesTitles,
        ILooksUpByName
{
    /// <summary>The provider this catalogue names itself as.</summary>
    internal const string ProviderName = "ThePornDB";

    /// <summary>The largest page the provider serves.</summary>
    /// <remarks>A larger number is refused with 422 rather than clamped down to this one.</remarks>
    internal const int MaxPerPage = 100;

    /// <summary>How many rows of any one catalogue the provider will serve at all.</summary>
    /// <remarks>
    /// A size reading this figure is a floor rather than a count, and the reachable set ends here: a
    /// page past the last one is clamped to the last and re-served with the clamped number echoed
    /// back, so paging beyond it repeats silently instead of erroring.
    /// </remarks>
    internal const int CatalogueCeiling = 10_000;

    /// <summary>How many search pages an exact-name lookup reads before it gives up.</summary>
    /// <remarks>
    /// The walk is bounded by the search result rather than by the library. A search is
    /// relevance-ordered, so an exact match not found within these pages is not found by reading on.
    /// </remarks>
    internal const int MaxLookupPages = 5;

    /// <summary>How many values a facet menu carries before it is offered as a type-ahead.</summary>
    /// <remarks>The tag set runs to thousands, so a menu read whole would grow with the provider.</remarks>
    internal const int FacetPageSize = 25;

    /// <summary>The key a year selection travels under, as the provider spells its own parameter.</summary>
    internal const string YearKey = "year";

    /// <summary>The key a tag selection travels under, as the provider spells its own parameter.</summary>
    internal const string TagFacetKey = "tags";

    /// <summary>The ordering the provider is read under when the surface names none.</summary>
    internal const string NewestFirst = "recently_released";

    /// <summary>The provider's own name for the reverse of <see cref="NewestFirst"/>.</summary>
    internal const string OldestFirst = "former_released";

    /// <summary>The earliest year the year menu offers.</summary>
    /// <remarks>
    /// A date before this is not a release date this provider carries, so a row spelling one is
    /// read as no year at all and fills the menu with nothing.
    /// </remarks>
    internal const int EarliestReleaseYear = 1970;

    private const string ScenesRoute = "scenes";
    private const string SitesRoute = "sites";
    private const string TagsRoute = "tags";
    private const string PerformersRoute = "performers";

    private readonly HttpClient _http;
    private readonly ProviderEndpointPort _endpoints;
    private readonly OptionsStore _options;
    private readonly ProviderPacer _pacer;
    private readonly ILogger _log;

    public ThePornDbCatalogue(
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
        Capabilities = ProviderCapabilities.ForThePornDb(this);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The provider's own vocabulary, which carries the direction inside each value and declares no
    /// title ordering. Title A to Z is therefore absent from this provider's menu.
    /// </remarks>
    public IReadOnlyList<ProviderSortOption> Sorts { get; } =
    [
        new(NewestFirst, "Newest first"),
        new(OldestFirst, "Oldest first"),
        new("duration_desc", "Longest first"),
        new("duration_asc", "Shortest first"),
    ];

    public ProviderCapabilitySet Capabilities { get; }

    public async Task<ProviderCatalogueAnswer> ReadPageAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveProviderAsync(ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return ProviderCatalogueAnswer.NotReached;
        }

        var scope = await ScopeForAsync(resolved, request, ct).ConfigureAwait(false);
        if (scope is null)
        {
            return ProviderCatalogueAnswer.NotReached;
        }

        var answered = await AskAsync(resolved, ScenesRoute, scope, ct).ConfigureAwait(false);
        if (answered is null)
        {
            return ProviderCatalogueAnswer.NotReached;
        }

        var scenes = new List<ProviderScene>();
        if (answered.Value.TryGetProperty("data", out var rows)
            && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (ProviderSceneProjector.ProjectThePornDbScene(row) is { } scene)
                {
                    scenes.Add(scene);
                }
            }
        }

        var meta = Meta(answered.Value);
        var size = Number(meta, "total") ?? 0;

        // The last page is the provider's own. Derived from the size it would offer pages past the
        // ceiling that silently re-serve the last one.
        return ProviderCatalogueAnswer.Answered(
            new ProviderCataloguePage(
                scenes,
                size,
                size >= CatalogueCeiling,
                Math.Max(Number(meta, "last_page") ?? 1, 1),
                Number(meta, "from") ?? 0,
                Number(meta, "to") ?? 0));
    }

    public async Task<int?> ReadCatalogueSizeAsync(
        ProviderCatalogueRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var resolved = await ResolveProviderAsync(ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        var scope = await ScopeForAsync(resolved, request, ct).ConfigureAwait(false);
        if (scope is null)
        {
            return null;
        }

        var answered = await AskAsync(resolved, ScenesRoute, scope, ct).ConfigureAwait(false);
        return answered is null ? null : Number(Meta(answered.Value), "total") ?? 0;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Each search route matches on a substring and orders by relevance, so the exact filter is this
    /// product's own. A tag answers the provider's numeric identifier, which is what the scene route
    /// accepts; a site and a performer answer the identifier Cove itself stores.
    /// </remarks>
    public async Task<ProviderIdentityLookup> LookUpByNameAsync(
        WhisparrEntityKind kind, string name, IReadOnlyList<string> aliases, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        var resolved = await ResolveProviderAsync(ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return ProviderIdentityLookup.Unmatched;
        }

        foreach (var candidate in Candidates(name, aliases))
        {
            var found = await SearchExactAsync(resolved, kind, candidate, ct).ConfigureAwait(false);
            if (found.IsAmbiguous || found.ProviderEntityId is not null)
            {
                return found;
            }
        }

        return ProviderIdentityLookup.Unmatched;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Two menus, each filled by asking the provider. Neither the performer route nor the site route
    /// exposes a filter that would scope its values to an entity, so neither of those menus is
    /// listable and both are absent.
    /// </remarks>
    public async Task<IReadOnlyList<ProviderFacetMenu>> ListFacetMenusAsync(
        WhisparrEntityKind kind, string providerEntityId, CancellationToken ct)
    {
        // A tag menu on a tag page narrows a tag to itself, and it is the only menu this provider
        // fills, so a tag page is asked for nothing at all.
        if (kind == WhisparrEntityKind.Tag)
        {
            return [];
        }

        var resolved = await ResolveProviderAsync(ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return [];
        }

        var menus = new List<ProviderFacetMenu>();

        if (await TagMenuAsync(resolved, ct).ConfigureAwait(false) is { } tags)
        {
            menus.Add(tags);
        }

        if (await YearMenuAsync(resolved, kind, providerEntityId, ct).ConfigureAwait(false)
            is { } years)
        {
            menus.Add(years);
        }

        return menus;
    }

    private async Task<ProviderFacetMenu?> TagMenuAsync(
        ResolvedProvider resolved, CancellationToken ct)
    {
        var answered = await AskAsync(
                resolved,
                TagsRoute,
                Query(("per_page", FacetPageSize.ToString(CultureInfo.InvariantCulture)), ("page", "1")),
                ct)
            .ConfigureAwait(false);

        if (answered is null
            || !answered.Value.TryGetProperty("data", out var rows)
            || rows.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<ProviderFacetValue>();
        foreach (var row in rows.EnumerateArray())
        {
            var id = Identifier(row, "id");
            var label = Text(row, "name");
            if (id is { Length: > 0 } && label is { Length: > 0 })
            {
                values.Add(new ProviderFacetValue(id, label));
            }
        }

        var offered = Number(Meta(answered.Value), "total") ?? values.Count;
        return new ProviderFacetMenu(TagFacetKey, "Tags", values, offered > values.Count);
    }

    /// <summary>Every year the entity's own catalogue spans, newest first.</summary>
    /// <remarks>
    /// Read as the two edges of the catalogue under the provider's own date orderings, so the menu
    /// covers the whole of it and offers no year it holds no scene in. Null where either edge could
    /// not be read.
    /// </remarks>
    private async Task<ProviderFacetMenu?> YearMenuAsync(
        ResolvedProvider resolved,
        WhisparrEntityKind kind,
        string providerEntityId,
        CancellationToken ct)
    {
        var scoped = await ScopeEntryAsync(resolved, EdgeRequest(kind, providerEntityId), ct)
            .ConfigureAwait(false);
        if (scoped is null)
        {
            return null;
        }

        var newest = await EdgeYearAsync(resolved, scoped.Value, NewestFirst, ct)
            .ConfigureAwait(false);
        var oldest = await EdgeYearAsync(resolved, scoped.Value, OldestFirst, ct)
            .ConfigureAwait(false);

        if (newest is not { } last || oldest is not { } first || first > last)
        {
            return null;
        }

        var values = new List<ProviderFacetValue>();
        for (var year = last; year >= first; year--)
        {
            var spelled = year.ToString(CultureInfo.InvariantCulture);
            values.Add(new ProviderFacetValue(spelled, spelled));
        }

        return new ProviderFacetMenu(YearKey, "Year", values, IsTypeAhead: false);
    }

    private async Task<int?> EdgeYearAsync(
        ResolvedProvider resolved,
        (string Key, string Value) scope,
        string ordering,
        CancellationToken ct)
    {
        var answered = await AskAsync(
                resolved,
                ScenesRoute,
                Query(scope, ("page", "1"), ("per_page", "1"), ("orderBy", ordering)),
                ct)
            .ConfigureAwait(false);

        return answered is not null
            && answered.Value.TryGetProperty("data", out var rows)
            && rows.ValueKind == JsonValueKind.Array
            && rows.GetArrayLength() > 0
                ? ReleaseYear(Text(rows[0], "date"))
                : null;
    }

    // The provider spells a release date as an ISO day. A year outside what a catalogue can carry is
    // read as no year, so one malformed row cannot stretch the menu over centuries.
    private static int? ReleaseYear(string? date)
        => date is { Length: >= 4 }
            && int.TryParse(
                date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year >= EarliestReleaseYear
            && year <= DateTime.UtcNow.Year
                ? year
                : null;

    private static ProviderCatalogueRequest EdgeRequest(
        WhisparrEntityKind kind, string providerEntityId)
        => new(kind, providerEntityId, 1, 1, null, null, new Dictionary<string, string>());

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

    private static JsonElement Meta(JsonElement answer)
        => answer.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
            ? meta
            : default;

    private static int? Number(JsonElement row, string property)
        => row.ValueKind == JsonValueKind.Object
            && row.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
                ? value.GetInt32()
                : null;

    private static string? Text(JsonElement row, string property)
        => row.ValueKind == JsonValueKind.Object
            && row.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    // An identifier the provider spells as a number on one route and as text on another.
    private static string? Identifier(JsonElement row, string property)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetInt32().ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static bool IsNumeric(string value)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static string Query(params (string Key, string Value)[] pairs)
    {
        var composed = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            composed.Append(composed.Length == 0 ? '?' : '&');
            composed.Append(Uri.EscapeDataString(key));
            composed.Append('=');
            composed.Append(Uri.EscapeDataString(value));
        }

        return composed.ToString();
    }

    private async Task<ResolvedProvider?> ResolveProviderAsync(CancellationToken ct)
    {
        var stored = await _options.LoadAsync(ct).ConfigureAwait(false);
        return _endpoints.Resolve(WhisparrGeneration.V2, stored.MetadataProviderEndpoints);
    }

    // Null where the entity has no identifier the scene route accepts, which is an answer about this
    // entity rather than about the provider.
    private async Task<string?> ScopeForAsync(
        ResolvedProvider resolved, ProviderCatalogueRequest request, CancellationToken ct)
    {
        var pairs = new List<(string Key, string Value)>
        {
            ("page", Math.Max(request.Page, 1).ToString(CultureInfo.InvariantCulture)),
            (
                "per_page",
                Math.Clamp(request.PerPage, 1, MaxPerPage).ToString(CultureInfo.InvariantCulture)
            ),
            ("orderBy", request.Sort is { Length: > 0 } sort ? sort : NewestFirst),
        };

        var scoped = await ScopeEntryAsync(resolved, request, ct).ConfigureAwait(false);
        if (scoped is null)
        {
            return null;
        }

        pairs.Add(scoped.Value);

        if (request.TitleSearch is { Length: > 0 } search)
        {
            pairs.Add(("title", search));
        }

        foreach (var (key, value) in request.Filters)
        {
            if (string.Equals(key, YearKey, StringComparison.Ordinal))
            {
                pairs.Add((YearKey, value));
            }
            else if (string.Equals(key, TagFacetKey, StringComparison.Ordinal))
            {
                pairs.Add(TagPair(value));
            }
        }

        return Query([.. pairs]);
    }

    // The provider reads the identifier out of the key and ignores the value, so the identifier is
    // sent as both rather than a name being carried for a field nothing reads.
    private static (string Key, string Value) TagPair(string tagId)
        => ($"{TagFacetKey}[{tagId}]", tagId);

    private async Task<(string Key, string Value)?> ScopeEntryAsync(
        ResolvedProvider resolved, ProviderCatalogueRequest request, CancellationToken ct)
    {
        var id = request.ProviderEntityId;
        switch (request.Kind)
        {
            case WhisparrEntityKind.Tag:
                // The tag routes carry no identifier segment, so a tag reaches this product only
                // through the exact-name lookup, which answers the numeric identifier.
                return IsNumeric(id) ? TagPair(id) : null;

            case WhisparrEntityKind.Performer:
                {
                    var numeric = IsNumeric(id)
                        ? id
                        : await NumericIdAsync(resolved, PerformersRoute, id, "_id", ct)
                            .ConfigureAwait(false);
                    return numeric is null ? null : ("performer_id", numeric);
                }

            default:
                {
                    var numeric = IsNumeric(id)
                        ? id
                        : await NumericIdAsync(resolved, SitesRoute, id, "id", ct).ConfigureAwait(false);
                    return numeric is null ? null : ("site_id", numeric);
                }
        }
    }

    // The scene route refuses the identifier Cove stores, so one read converts it. Nothing is held
    // between requests: a cache here would answer for a source the host was reconfigured away from.
    private async Task<string?> NumericIdAsync(
        ResolvedProvider resolved,
        string collection,
        string storedId,
        string property,
        CancellationToken ct)
    {
        var answered = await AskAsync(
                resolved, $"{collection}/{Uri.EscapeDataString(storedId)}", string.Empty, ct)
            .ConfigureAwait(false);

        return answered is not null
            && answered.Value.TryGetProperty("data", out var entity)
            && entity.ValueKind == JsonValueKind.Object
                ? Identifier(entity, property)
                : null;
    }

    private async Task<ProviderIdentityLookup> SearchExactAsync(
        ResolvedProvider resolved, WhisparrEntityKind kind, string term, CancellationToken ct)
    {
        var (collection, property) = kind switch
        {
            WhisparrEntityKind.Tag => (TagsRoute, "id"),
            WhisparrEntityKind.Performer => (PerformersRoute, "id"),
            _ => (SitesRoute, "uuid"),
        };

        for (var page = 1; page <= MaxLookupPages; page++)
        {
            var answered = await AskAsync(
                    resolved,
                    collection,
                    Query(("q", term), ("page", page.ToString(CultureInfo.InvariantCulture))),
                    ct)
                .ConfigureAwait(false);

            if (answered is null
                || !answered.Value.TryGetProperty("data", out var rows)
                || rows.ValueKind != JsonValueKind.Array)
            {
                return ProviderIdentityLookup.Unmatched;
            }

            var matched = new List<string>();
            foreach (var row in rows.EnumerateArray())
            {
                if (string.Equals(Text(row, "name"), term, StringComparison.OrdinalIgnoreCase)
                    && Identifier(row, property) is { Length: > 0 } id)
                {
                    matched.Add(id);
                }
            }

            if (matched.Count > 1)
            {
                return ProviderIdentityLookup.Ambiguous;
            }

            if (matched.Count == 1)
            {
                return ProviderIdentityLookup.Matched(matched[0]);
            }

            if (page >= (Number(Meta(answered.Value), "last_page") ?? 1))
            {
                break;
            }
        }

        return ProviderIdentityLookup.Unmatched;
    }

    // Null where no catalogue arrived: no whole answer, a status that is not a success, or a body
    // carrying the provider's own refusal. The body decides, so a refusal is never read as a
    // catalogue that is simply empty. An answer the provider stated ends the attempts, because
    // re-sending collects the same refusal and writes the same line again.
    private async Task<JsonElement?> AskAsync(
        ResolvedProvider resolved, string collection, string query, CancellationToken ct)
    {
        var attempts = WhisparrRetryPolicy.AttemptsFor(WhisparrVerbClass.Read);
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var read = await TryReadAsync(resolved, collection, query, ct).ConfigureAwait(false);
            if (read.Body is { } carried)
            {
                return carried;
            }

            if (read.WasDefinite)
            {
                return null;
            }
        }

        return null;
    }

    private async Task<ProviderSend> TryReadAsync(
        ResolvedProvider resolved, string collection, string query, CancellationToken ct)
    {
        if (!await _pacer.WaitForTurnAsync(resolved.MaxRequestsPerMinute, ct).ConfigureAwait(false))
        {
            return ProviderSend.Nothing;
        }

        // The API address rather than the configured one: the configured spelling is where identity
        // rows are stamped, and this provider serves no catalogue there.
        using var message = new HttpRequestMessage(
            HttpMethod.Get, $"{ProviderApiBase.ThePornDb}/{collection}{query}");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", resolved.ApiKey);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // The whole attempt is bounded here, headers and body alike. The client's own timeout stops
        // at the headers once the body is asked for separately.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_http.Timeout);

        try
        {
            using var response = await _http
                .SendAsync(message, HttpCompletionOption.ResponseHeadersRead, attempt.Token)
                .ConfigureAwait(false);

            var answered = await ProviderResponseBound.ReadAsync(response.Content, attempt.Token)
                .ConfigureAwait(false);
            if (answered is null)
            {
                WhisparrSyncLog.ProviderAnswerBeyondReadBound(
                    _log, ProviderName, WhisparrClient.MaxResponseBytes);
                return ProviderSend.Nothing;
            }

            return Parse(answered, response.IsSuccessStatusCode);
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

    private ProviderSend Parse(string answered, bool wasSuccess)
    {
        // A status outside the success range is the provider's own answer whatever the body says,
        // so an unreadable body under one is still not worth another attempt.
        var undecided = wasSuccess ? ProviderSend.Nothing : ProviderSend.Refused;

        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(answered);
        }
        catch (JsonException)
        {
            return undecided;
        }

        using (parsed)
        {
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return undecided;
            }

            if (root.TryGetProperty("errors", out _) || root.TryGetProperty("message", out _))
            {
                WhisparrSyncLog.ProviderRefusedTheQuery(_log, ProviderName);
                return ProviderSend.Refused;
            }

            return wasSuccess && root.TryGetProperty("data", out _)
                ? ProviderSend.Carrying(root.Clone())
                : undecided;
        }
    }
}
