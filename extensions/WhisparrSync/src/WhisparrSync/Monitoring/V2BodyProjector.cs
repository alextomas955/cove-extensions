using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Whisparr2.Net.Model;
using WhisparrSync.Contracts;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Monitoring;

/// <summary>The bodies v2 is sent, composed rather than assembled at a call site.</summary>
/// <remarks>
/// Pure. Every flag that suppresses acquisition is set here, from ONE local, so an edit cannot set one
/// spelling and miss the other. v2's pair is not v3's: a rule stated in the
/// newer spellings leaves every body composed here unguarded.
/// <para>
/// This generation addresses a studio as a series and its catalogue as years, which is why the wire
/// field names below read the way they do. A wire field name is not user-facing wording, and no
/// sentence a user reads is composed here.
/// </para>
/// <para>
/// A scope change on this generation is retroactive: re-applying a monitoring option rewrites the flag
/// on every year the instance already holds, in both directions. On v3 the equivalent
/// gates only what a later catalogue read adds. A reader who assumes the two behave alike will be
/// wrong about one of them.
/// </para>
/// </remarks>
internal static class V2BodyProjector
{
    /// <summary>This generation's search command. The one verb that downloads.</summary>
    internal const string SeriesSearchCommand = "SeriesSearch";

    /// <summary>The command asking the instance to look for what one entity monitors and lacks.</summary>
    /// <remarks>
    /// Composed only for a caller holding the grabbing role. It is the one body this product can
    /// compose that makes an instance acquire anything.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entityId"/> is below one.</exception>
    internal static JsonObject SearchMonitored(int entityId)
        => Command(SeriesSearchCommand, entityId);

    /// <summary>This generation's catalogue-refresh command.</summary>
    internal const string RefreshSeriesCommand = "RefreshSeries";

    /// <summary>The command asking the instance to re-read one entity's catalogue.</summary>
    /// <inheritdoc cref="Command" path="/remarks"/>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entityId"/> is below one.</exception>
    internal static JsonObject RefreshCatalogue(int entityId)
        => Command(RefreshSeriesCommand, entityId);

    /// <summary>One command naming one entity, in this generation's scalar spelling.</summary>
    /// <remarks>
    /// A single scalar id, not an array. The other generation names an id array, and a body carrying
    /// the other's shape is accepted and does nothing at all.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entityId"/> is below one.</exception>
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

    /// <summary>The term the lookup is asked for <paramref name="storedId"/> under.</summary>
    /// <remarks>
    /// The stored identifier exactly as the library holds it, with no prefix and no scheme. The
    /// prefixed spelling this generation's own documentation suggests expects its numeric form: given
    /// the identifier the library holds it answers with a success and an empty list, so a prefixed term
    /// matches nothing and reports no failure of any kind.
    /// </remarks>
    internal static string LookupTerm(string storedId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedId);

        // Never "tpdb:" + storedId. That form is answered 200 with an empty array and the miss is
        // completely silent.
        return storedId.Trim();
    }

    /// <summary>
    /// Adds the entity <paramref name="entityId"/> names, monitored at <paramref name="scope"/>.
    /// </summary>
    /// <remarks>
    /// Composed field for field as this generation's own form composes it. The identifier is the
    /// number the metadata source names the site by, which travels in a field this generation
    /// misnames after an unrelated metadata source.
    /// <para>
    /// The new-item rule is set to the whole catalogue because that is this generation's own default,
    /// and it governs whether a catalogue addition made later is monitored, which is a different
    /// question from the scope.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scope"/> is not a scope this product expresses, or <paramref name="defaults"/>
    /// names no usable quality profile. This generation refuses a zero profile with a validation
    /// failure naming the property, and v3 accepts it and then never acquires.
    /// </exception>
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

    /// <summary>The name an add carries for the site <paramref name="entityId"/> names.</summary>
    /// <remarks>
    /// The instance refuses an add carrying no title, with a validation failure naming the property,
    /// and discards the value of the one it is given: it resolves the site's real title and its slug
    /// from the number alone. So the number rendered as text satisfies the refusal and asserts
    /// nothing this product would have had to be right about.
    /// <para>
    /// The slug member is passed null rather than composed. Null is the member's absence on the wire,
    /// not a value this product chose, and an add carrying no slug at all is accepted.
    /// </para>
    /// </remarks>
    private static string NameFor(int entityId)
        => entityId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Registers the entity <paramref name="entityId"/> names, monitoring nothing.</summary>
    /// <remarks>
    /// Presence only. The monitored flag is off, the new-item rule is none and the add-time monitor
    /// covers none of the catalogue, so nothing the instance then reads for the entity is wanted.
    /// A whole studio's catalogue arrives with it, and the alternative would want every scene in it.
    /// <para>
    /// Composed in this generation's own spellings. The other generation's suppression member is one
    /// this generation discards without saying so, so a body carrying it would suppress nothing.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="defaults"/> names no usable quality profile. This generation refuses a zero
    /// profile with a validation failure naming the property.
    /// </exception>
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

    /// <summary>Sets only the monitored flag on the entity <paramref name="entityId"/> names.</summary>
    /// <remarks>
    /// Every member of the editor resource this leaves unset is omitted from the wire document, and an
    /// omitted one is not applied, so the profile, the path, the tags, the new-item rule and every
    /// per-year flag the instance holds are all left alone.
    /// </remarks>
    internal static SeriesEditorResource SetMonitored(int entityId, bool monitored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new SeriesEditorResource(seriesIds: new List<int> { entityId }, monitored: monitored);
    }

    /// <summary>
    /// Sets only the monitored flag on the row <paramref name="rowId"/> names, and on nothing else.
    /// </summary>
    /// <remarks>
    /// The instance's own row id and the flag, and no other member. This generation names a scene
    /// only as a row under a site, so the id is the one its own list answered with rather than the
    /// number the metadata provider issued.
    /// <para>
    /// A list of exactly one row. The route takes a list, and a body naming several would set the
    /// flag on every one of them.
    /// </para>
    /// </remarks>
    internal static EpisodesMonitoredResource MonitorScene(int rowId, bool monitored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rowId, 1);
        return new EpisodesMonitoredResource(
            episodeIds: new List<int> { rowId }, monitored: monitored);
    }

    /// <summary>Re-applies <paramref name="scope"/> over what the instance already holds.</summary>
    /// <remarks>
    /// The entity is named inside an array of objects rather than as a scalar. The route answers a body
    /// it cannot read with a server failure and an empty body, so the shape is the whole of what makes
    /// the request expressible.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scope"/> is not a scope this product expresses.
    /// </exception>
    internal static SeasonPassResource SetScope(int entityId, MonitorScope scope)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new SeasonPassResource(
            series: new List<SeasonPassSeriesResource> { new(id: entityId) },
            monitoringOptions: new MonitoringOptions(monitor: CatalogueTypeFor(scope)));
    }

    /// <summary>How much of a catalogue <paramref name="scope"/> covers, as this generation types it.</summary>
    /// <remarks>
    /// Two keys and no others. This generation's own dropdown offers nine more — a missing-only, an
    /// existing-only, a recent-only, a first and a latest entry, a pilot entry, two specials entries
    /// and an off entry — four of which it renders to a user as raw localization keys. Mimicry stops
    /// where the interface being mimicked is defective, and none of the nine is composable here.
    /// </remarks>
    private static MonitorTypes CatalogueTypeFor(MonitorScope scope)
        => scope switch
        {
            MonitorScope.FutureScenes => MonitorTypes.Future,
            MonitorScope.AllScenes => MonitorTypes.All,
            _ => throw new ArgumentOutOfRangeException(
                nameof(scope), scope, "This is not a monitor scope this product expresses."),
        };
}

/// <summary>What one entity is called on v2, once its lookup has answered.</summary>
/// <param name="EntityId">The numeric identifier the lookup answered with.</param>
/// <param name="Title">The name the lookup answered with.</param>
/// <param name="TitleSlug">The slug the add is composed with.</param>
internal sealed record V2Site(int EntityId, string Title, string TitleSlug);

/// <summary>What a v2 lookup answered.</summary>
internal enum V2LookupReading
{
    /// <summary>Exactly one entity answered, and it is named.</summary>
    Resolved,

    /// <summary>Nothing answered. The identifier names no entity this generation knows.</summary>
    NoMatch,

    /// <summary>More than one entity answered, and nothing says which was meant.</summary>
    Ambiguous,

    /// <summary>The answer is not a list of entities at all.</summary>
    Unreadable,
}

/// <summary>The entity a lookup named, or why it named none.</summary>
/// <param name="Site">The entity, or null on anything but a single answer.</param>
/// <param name="Reading">What the answer was.</param>
internal sealed record V2SiteResolution(V2Site? Site, V2LookupReading Reading);

/// <summary>What v2's lookup and listing answers mean.</summary>
/// <remarks>
/// Pure. Read on the parsed shape and never on a status: this generation answers an identifier it does
/// not know with a success and an empty list, and answers a body whose fields it dropped with a created
/// status and an echo. A status is not evidence about this generation.
/// <para>
/// The answer never echoes the term it was asked about, so the correspondence between the identifier
/// the library holds and the entity acted on rests on there being exactly ONE answer rather than on any
/// field matching what was sent. More than one is therefore a refusal and not a pick of the first.
/// </para>
/// </remarks>
internal static class V2LookupProjector
{
    /// <summary>The entity <paramref name="body"/> names, or why it names none.</summary>
    internal static V2SiteResolution Resolve(string? body)
    {
        if (AsArray(body) is not { } answered)
        {
            return new V2SiteResolution(null, V2LookupReading.Unreadable);
        }

        if (answered.Count == 0)
        {
            return new V2SiteResolution(null, V2LookupReading.NoMatch);
        }

        if (answered.Count > 1)
        {
            return new V2SiteResolution(null, V2LookupReading.Ambiguous);
        }

        return SiteIn(answered[0]) is { } site
            ? new V2SiteResolution(site, V2LookupReading.Resolved)
            : new V2SiteResolution(null, V2LookupReading.Unreadable);
    }

    /// <summary>What a caller answers a <paramref name="reading"/> with.</summary>
    /// <remarks>
    /// An empty answer is the no-identity refusal rather than an instance one: the entity carries an
    /// identifier the library holds and this generation's own source does not know it.
    /// </remarks>
    internal static MonitorRefusalKind RefusalFor(V2LookupReading reading)
        => reading switch
        {
            V2LookupReading.Resolved => MonitorRefusalKind.None,
            V2LookupReading.NoMatch => MonitorRefusalKind.NoIdentityInThisNamespace,
            _ => MonitorRefusalKind.InstanceRefused,
        };

    /// <summary>
    /// The entity <paramref name="entityId"/> names inside <paramref name="listed"/>, or null when the
    /// instance holds none.
    /// </summary>
    /// <remarks>
    /// The lookup answers with no instance-side identifier until an entity has been added, so whether
    /// the instance holds it is a second question and its own listing is what answers it. Only the
    /// matched entry is returned.
    /// <para>
    /// The match stays even where the listing was asked for one entity. The answer is read on its
    /// parsed shape and never on the fact that a filter was asked for, because this generation
    /// publishes no contract and accepting a query it silently ignored would return an entity nobody
    /// named.
    /// </para>
    /// </remarks>
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

    /// <summary>
    /// Which of <paramref name="asked"/> the site's own rows in <paramref name="listed"/> name, and
    /// each row's own identifier, or null where the answer is not a list of rows at all.
    /// </summary>
    /// <remarks>
    /// The answer is bounded by <paramref name="asked"/> and by nothing the instance sent, so a
    /// site's whole catalogue reduces to at most as many entries as were asked about. Each row is
    /// read and dropped, so nothing here grows with the site.
    /// <para>
    /// A number the list does not carry is absent from the answer rather than present with a zero.
    /// The two would read the same at a caller that looked the number up and got a default.
    /// </para>
    /// </remarks>
    internal static IReadOnlyDictionary<int, int>? SiteSceneRows(
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

    private static V2Site? SiteIn(JsonNode? answered)
    {
        if (answered is not JsonObject site
            || site["tvdbId"] is not JsonValue named
            || !named.TryGetValue<int>(out var entityId)
            || entityId < 1)
        {
            return null;
        }

        // Read the same way the identifier above is. This generation publishes no contract, so a
        // field's type cannot be assumed, and a node that is not a string reads as unreadable rather
        // than throwing out of the seam.
        var title = site["title"] is JsonValue titled && titled.TryGetValue<string>(out var name)
            ? name
            : null;
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // A slug the answer does not carry and a slug of the wrong type are different facts. The
        // field is the instance's own, and a site it does not yet hold has no row to have set one on,
        // so an absent slug is what every registration starts from: requiring one made a site
        // resolvable only once it was already held, which is the opposite of what registering is for.
        // A slug that IS there and is not a string says the answer is not the shape this reads, which
        // is unreadable for the same reason a mistyped identifier is.
        var carried = site["titleSlug"];
        if (carried is not null && carried.GetValueKind() is not JsonValueKind.Null)
        {
            return carried is JsonValue slugged
                && slugged.TryGetValue<string>(out var slug)
                && !string.IsNullOrWhiteSpace(slug)
                    ? new V2Site(entityId, title, slug)
                    : null;
        }

        // Composed as the instance composes its own: every row it holds carries a slug equal to its
        // identifier.
        return new V2Site(entityId, title, entityId.ToString(CultureInfo.InvariantCulture));
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
