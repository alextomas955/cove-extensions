using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Whisparr2.Net.Model;
using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

// Both acquisition-suppressing flags are set here from one local, so an edit cannot set one
// spelling and miss the other. v2's pair is not v3's: a rule stated in v3's spellings leaves every
// body here unguarded. This generation addresses a studio as a series and its catalogue as years,
// which is why the wire names below read as they do; no sentence a user reads is composed here.
//
// A scope change here is retroactive, rewriting the flag on every year the instance holds, in both
// directions. The v3 equivalent gates only what a later catalogue read adds.
internal static class V2BodyProjector
{
    // The one verb that downloads.
    internal const string SeriesSearchCommand = "SeriesSearch";

    // Composed only for a caller holding the grabbing role: this is a body that makes an instance
    // acquire.
    internal static JsonObject SearchMonitored(int entityId)
        => Command(SeriesSearchCommand, entityId);

    internal const string RefreshSeriesCommand = "RefreshSeries";

    internal static JsonObject RefreshCatalogue(int entityId)
        => Command(RefreshSeriesCommand, entityId);

    // A single scalar id, not an array. v3 names an id array, and a body carrying the other's
    // shape is accepted and does nothing.
    internal static JsonObject Command(string name, int entityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new JsonObject
        {
            ["name"] = name,
            ["seriesId"] = entityId,
        };
    }

    // The identifier is the number the metadata source names the site by, which travels in a field
    // this generation misnames after an unrelated metadata source.
    //
    // The new-item rule is the whole catalogue, this generation's own default. It governs whether a
    // catalogue addition made later is monitored, which is a different question from the scope.
    internal static SeriesResource AddStudio(int entityId, MonitorScope scope, AddDefaults defaults)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaults.RootFolderPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaults.QualityProfileId, 1);

        const bool search = false;
        return new SeriesResource(
            tvdbId: entityId,
            title: NameFor(entityId),
            titleSlug: null,
            qualityProfileId: defaults.QualityProfileId,
            rootFolderPath: defaults.RootFolderPath,
            monitored: true,
            monitorNewItems: NewItemMonitorTypes.All,
            seriesType: SeriesTypes.Standard,
            seasons: new List<SeasonResource>(),
            tags: new List<int>(),
            addOptions: new AddSeriesOptions(
                monitor: CatalogueTypeFor(scope),
                searchForMissingEpisodes: search,
                searchForCutoffUnmetEpisodes: search));
    }

    // The instance refuses an add carrying no title and discards the value of the one it is given,
    // resolving the site's real title and slug from the number alone. The number rendered as text
    // satisfies the refusal and asserts nothing. The slug member is passed null, which is its
    // absence on the wire, and an add carrying no slug is accepted.
    private static string NameFor(int entityId)
        => entityId.ToString(CultureInfo.InvariantCulture);

    // Presence only: the monitored flag is off, the new-item rule is none and the add-time monitor
    // covers none of the catalogue, so nothing the instance reads for the entity is wanted. A whole
    // studio's catalogue arrives with it, and the alternative would want every scene in it.
    //
    // Composed in this generation's spellings. v3's suppression member is one this generation
    // discards without saying so, so a body carrying it would suppress nothing.
    internal static SeriesResource RegisterSite(int entityId, AddDefaults defaults)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaults.RootFolderPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaults.QualityProfileId, 1);

        const bool search = false;
        return new SeriesResource(
            tvdbId: entityId,
            title: NameFor(entityId),
            titleSlug: null,
            qualityProfileId: defaults.QualityProfileId,
            rootFolderPath: defaults.RootFolderPath,
            monitored: false,
            monitorNewItems: NewItemMonitorTypes.None,
            seriesType: SeriesTypes.Standard,
            seasons: new List<SeasonResource>(),
            tags: new List<int>(),
            addOptions: new AddSeriesOptions(
                monitor: MonitorTypes.None,
                searchForMissingEpisodes: search,
                searchForCutoffUnmetEpisodes: search));
    }

    // The resource the instance answered, changed in place: rebuilding would name a fixed member
    // set and drop everything outside it, the tags, the per-year flags, whatever a later client
    // carries.
    //
    // The path relocates a site; changing the root folder alone is accepted and relocates nothing.
    // The instance derives the root from the path, so the path is recomposed as the new root plus
    // the site's existing last segment, with the root sent beside it to state the intent. Nothing
    // here instructs a transfer: whether files move is a parameter of the request, not of this body.
    internal static SeriesResource MovedSiteRoot(SeriesResource held, string instanceRoot)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(held.Path);

        var separator = SeparatorOf(held.Path);
        var root = Respelled(instanceRoot, separator);

        held.Path = Under(root, held.Path, separator);
        held.RootFolderPath = root;
        return held;
    }

    private static string Under(string root, string path, char separator)
    {
        var trimmedPath = path.TrimEnd('/', '\\');
        var lastSegment = trimmedPath[(trimmedPath.LastIndexOfAny(['/', '\\']) + 1)..];

        return lastSegment.Length == 0
            ? root
            : string.Create(CultureInfo.InvariantCulture, $"{root}{separator}{lastSegment}");
    }

    // The separator comes off the site's own held path rather than off the root. The root arrives
    // from the addressing port, which spells every candidate with forward slashes whichever host the
    // instance runs on, and joining a Windows path with a forward slash yields one that instance
    // will not resolve.
    private static char SeparatorOf(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        return trimmed.Contains('\\', StringComparison.Ordinal)
            && !trimmed.Contains('/', StringComparison.Ordinal)
                ? '\\'
                : '/';
    }

    private static string Respelled(string root, char separator)
        => root.TrimEnd('/', '\\').Replace(separator == '\\' ? '/' : '\\', separator);

    // Every member left unset is omitted from the wire document, and an omitted one is not
    // applied, so the profile, path, tags, new-item rule and per-year flags are left alone.
    internal static SeriesEditorResource SetMonitored(int entityId, bool monitored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new SeriesEditorResource(seriesIds: new List<int> { entityId }, monitored: monitored);
    }

    // This generation names a scene only as a row under a site, so the id is the one its own list
    // answered with, not the number the metadata provider issued. The route takes a list, and a
    // body naming several rows would set the flag on every one of them.
    internal static EpisodesMonitoredResource MonitorScene(int rowId, bool monitored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rowId, 1);
        return new EpisodesMonitoredResource(
            episodeIds: new List<int> { rowId }, monitored: monitored);
    }

    // The entity is named inside an array of objects, not as a scalar. The route answers a body it
    // cannot read with a server failure and an empty body.
    internal static SeasonPassResource SetScope(int entityId, MonitorScope scope)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new SeasonPassResource(
            series: new List<SeasonPassSeriesResource> { new(id: entityId) },
            monitoringOptions: new MonitoringOptions(monitor: CatalogueTypeFor(scope)));
    }

    // Two keys and no others. This generation's own dropdown offers nine more, four of which it
    // renders to a user as raw localization keys. None of the nine is composable here.
    private static MonitorTypes CatalogueTypeFor(MonitorScope scope)
        => scope switch
        {
            MonitorScope.FutureScenes => MonitorTypes.Future,
            MonitorScope.AllScenes => MonitorTypes.All,
            _ => throw new ArgumentOutOfRangeException(
                nameof(scope), scope, "This is not a monitor scope this product expresses."),
        };
}

// Read on the parsed shape and never on a status: this generation publishes no contract and
// answers a body whose fields it dropped with a created status and an echo.
internal static class V2ListProjector
{
    // The instance's own list is the one route that answers whether it holds the entity, and the
    // row it answers with is the only place its instance-side identifier appears. The match is made
    // here even where the listing was asked for one entity, because accepting a query the instance
    // silently ignored would return an entity nobody named.
    /// <summary>The one site a lookup answered, or null where it answered none.</summary>
    /// <remarks>
    /// Ordered by the lookup's own relevance and asked by an exact identifier, so the first entry is
    /// the site asked about.
    /// </remarks>
    internal static JsonObject? LookupEntry(string? answered)
        => AsArray(answered) is { Count: > 0 } rows ? rows[0] as JsonObject : null;

    /// <summary>The id the instance holds the site under, or zero where it holds none.</summary>
    internal static int InstanceRowIdIn(JsonObject site)
    {
        ArgumentNullException.ThrowIfNull(site);

        return site["id"] is JsonValue numbered && numbered.TryGetValue<int>(out var rowId)
            ? rowId
            : 0;
    }

    internal static JsonObject? HeldEntry(string? listed, int entityId)
    {
        if (AsArray(listed) is not { } held)
        {
            return null;
        }

        foreach (var entry in held)
        {
            if (entry is JsonObject candidate
                && candidate["tvdbId"] is JsonValue named
                && named.TryGetValue<int>(out var listedId)
                && listedId == entityId)
            {
                return candidate;
            }
        }

        return null;
    }

    // One reduction over both of this generation's lists. A site row and a scene row name
    // themselves by the same misnamed member and carry their instance-side id under the same one.
    //
    // The answer is bounded by what was asked and by nothing the instance sent, so a whole
    // catalogue reduces to at most as many entries as were asked about, and nothing grows with what
    // the instance holds. A number the list does not carry is absent rather than present with a
    // zero, which a caller could not tell from a default.
    internal static IReadOnlyDictionary<int, int>? RowsByNumber(
        string? listed, IReadOnlyCollection<int> asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        if (AsArray(listed) is not { } rows)
        {
            return null;
        }

        var wanted = asked.ToHashSet();
        var found = new Dictionary<int, int>();
        foreach (var row in rows)
        {
            if (row is not JsonObject entry
                || entry["tvdbId"] is not JsonValue numbered
                || !numbered.TryGetValue<int>(out var sceneNumber)
                || !wanted.Contains(sceneNumber)
                || entry["id"] is not JsonValue identified
                || !identified.TryGetValue<int>(out var rowId)
                || rowId < 1)
            {
                continue;
            }

            found[sceneNumber] = rowId;
        }

        return found;
    }

    private static JsonArray? AsArray(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(body) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
