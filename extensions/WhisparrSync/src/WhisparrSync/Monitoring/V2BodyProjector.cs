using System.Text.Json;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;

namespace WhisparrSync.Monitoring;

/// <summary>The bodies the older generation is sent, composed rather than assembled at a call site.</summary>
/// <remarks>
/// Pure. Every flag that suppresses acquisition is set here, from ONE local, so an edit cannot set one
/// spelling and miss the other. This generation's pair is not the newer one's: a rule stated in the
/// newer spellings leaves every body composed here unguarded.
/// <para>
/// This generation addresses a studio as a series and its catalogue as years, which is why the wire
/// field names below read the way they do. A wire field name is not user-facing wording, and no
/// sentence a user reads is composed here.
/// </para>
/// <para>
/// A scope change on this generation is retroactive: re-applying a monitoring option rewrites the flag
/// on every year the instance already holds, in both directions. On the newer generation the equivalent
/// gates only what a later catalogue read adds. A reader who assumes the two behave alike will be
/// wrong about one of them.
/// </para>
/// </remarks>
internal static class V2BodyProjector
{
    /// <summary>What every add and every scope change spells the whole catalogue with.</summary>
    private const string WholeCatalogue = "all";

    /// <summary>What every add and every scope change spells a future-only catalogue with.</summary>
    private const string FutureCatalogueOnly = "future";

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
    /// numeric one the lookup answered with, which arrives in a field this generation misnames after
    /// an unrelated metadata source.
    /// <para>
    /// The new-item rule is set to the whole catalogue because that is this generation's own default,
    /// and it governs whether a catalogue addition made later is monitored, which is a different
    /// question from the scope.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="scope"/> is not a scope this product expresses, or <paramref name="defaults"/>
    /// names no usable quality profile. This generation refuses a zero profile with a validation
    /// failure naming the property, and the newer one accepts it and then never acquires.
    /// </exception>
    internal static JsonObject AddStudio(
        int entityId, string title, string titleSlug, MonitorScope scope, AddDefaults defaults)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(titleSlug);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaults.RootFolderPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaults.QualityProfileId, 1);

        var monitor = CatalogueKeyFor(scope);
        const bool search = false;
        return new JsonObject
        {
            ["tvdbId"] = entityId,
            ["title"] = title,
            ["titleSlug"] = titleSlug,
            ["qualityProfileId"] = defaults.QualityProfileId,
            ["rootFolderPath"] = defaults.RootFolderPath,
            ["monitored"] = true,
            ["monitorNewItems"] = WholeCatalogue,
            ["seriesType"] = "standard",
            ["seasons"] = new JsonArray(),
            ["tags"] = new JsonArray(),
            ["addOptions"] = new JsonObject
            {
                ["monitor"] = monitor,
                ["searchForMissingEpisodes"] = search,
                ["searchForCutoffUnmetEpisodes"] = search,
            },
        };
    }

    /// <summary>Sets only the monitored flag on the entity <paramref name="entityId"/> names.</summary>
    /// <remarks>
    /// Every other field of the editor resource is nullable and an omitted one is not applied, so the
    /// profile, the path, the tags, the new-item rule and every per-year flag the instance holds are
    /// all left alone.
    /// </remarks>
    internal static JsonObject SetMonitored(int entityId, bool monitored)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new JsonObject
        {
            ["seriesIds"] = new JsonArray(entityId),
            ["monitored"] = monitored,
        };
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
    internal static JsonObject SetScope(int entityId, MonitorScope scope)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(entityId, 1);
        return new JsonObject
        {
            ["series"] = new JsonArray(new JsonObject { ["id"] = entityId }),
            ["monitoringOptions"] = new JsonObject { ["monitor"] = CatalogueKeyFor(scope) },
        };
    }

    /// <summary>How much of a catalogue <paramref name="scope"/> covers, in this generation's key.</summary>
    /// <remarks>
    /// Two keys and no others. This generation's own dropdown offers nine more — a missing-only, an
    /// existing-only, a recent-only, a first and a latest entry, a pilot entry, two specials entries
    /// and an off entry — four of which it renders to a user as raw localization keys. Mimicry stops
    /// where the interface being mimicked is defective, and none of the nine is composable here.
    /// </remarks>
    private static string CatalogueKeyFor(MonitorScope scope)
        => scope switch
        {
            MonitorScope.FutureScenes => FutureCatalogueOnly,
            MonitorScope.AllScenes => WholeCatalogue,
            _ => throw new ArgumentOutOfRangeException(
                nameof(scope), scope, "This is not a monitor scope this product expresses."),
        };
}

/// <summary>What one entity is called on the older generation, once its lookup has answered.</summary>
/// <param name="EntityId">The numeric identifier the lookup answered with.</param>
/// <param name="Title">The name the lookup answered with.</param>
/// <param name="TitleSlug">The slug the add is composed with.</param>
internal sealed record V2Site(int EntityId, string Title, string TitleSlug);

/// <summary>What an older-generation lookup answered.</summary>
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

/// <summary>What the older generation's lookup and listing answers mean.</summary>
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
    /// matched entry is returned: the listing grows with what the instance holds and nothing carries
    /// that onward.
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

    private static V2Site? SiteIn(JsonNode? answered)
    {
        if (answered is not JsonObject site
            || site["tvdbId"] is not JsonValue named
            || !named.TryGetValue<int>(out var entityId)
            || entityId < 1)
        {
            return null;
        }

        var title = site["title"]?.GetValue<string>();
        var titleSlug = site["titleSlug"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(titleSlug)
            ? null
            : new V2Site(entityId, title, titleSlug);
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
