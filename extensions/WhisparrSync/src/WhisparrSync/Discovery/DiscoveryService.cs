using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.SceneStatus;

namespace WhisparrSync.Discovery;

/// <summary>
/// The pure, I/O-free discovery diff: <c>catalogue − Cove-owned(by id) − excluded</c>. It holds NO client, DB,
/// or store handle — a metadata call is not merely avoided but STRUCTURALLY impossible here, mirroring
/// <see cref="SceneStatusProjector"/> — and takes only pre-fetched collections: the attributed catalogue, the
/// entity's owned Cove videos, and the Whisparr exclusion set. Projects each survivor into the camelCase
/// <see cref="MissingScene"/> wire DTO.
/// </summary>
/// <remarks>
/// The subtraction is by STABLE SOURCE ID (the connected version's id family — StashDB on v3, TPDB on v2, the
/// same key <see cref="SceneStatusProjector"/> matches on), NEVER the movie row's <c>hasFile</c> flag: an owned
/// scene whose file lives outside a Whisparr root reads <c>hasFile:false</c> yet is emphatically NOT missing, so
/// filtering on <c>hasFile</c> would list scenes Cove already owns. Owned-by-id therefore beats a fileless
/// Whisparr row.
/// <para>
/// DO NOT RESHAPE THE LOOP. Two structural properties of it are load-bearing for callers, and neither is
/// readable from the signature. First, the survivors are appended in catalogue ENCOUNTER ORDER: a catalogue the
/// metadata source already ordered therefore yields a correctly ordered missing subsequence, which is what makes
/// a source-applied ordering exact over the whole catalogue and not merely over the rows one page happened to
/// carry. Second, no survivor's fate depends on any OTHER row — the predicate reads only the owned and excluded
/// id sets — which makes the diff of a source-FILTERED catalogue identical to the filtered diff of the whole
/// one, and a source-applied facet exact for the same reason.
/// </para>
/// <para>
/// Reordering the survivors, capping them, de-duplicating them, or letting a row's own content decide its fate
/// each break one of the two IN SILENCE: nothing throws, and the rendered list simply stops agreeing with the
/// source it came from. <c>Diff_preserves_catalogue_encounter_order</c> and
/// <c>Diff_commutes_with_a_row_level_facet_predicate</c> exist to hold them.
/// </para>
/// </remarks>
internal static class DiscoveryService
{
    private static readonly IReadOnlyDictionary<string, WhisparrMovie> EmptyStatusIndex =
        new Dictionary<string, WhisparrMovie>(0);

    public static IReadOnlyList<MissingScene> Diff(
        IReadOnlyList<WhisparrMovie> catalogue,
        IReadOnlyList<CoveVideo> ownedVideos,
        IReadOnlyList<WhisparrExclusion> exclusions,
        string? entityName,
        DiscoveryIdFamily idFamily = DiscoveryIdFamily.StashDb,
        IReadOnlyDictionary<string, WhisparrMovie>? statusIndex = null,
        PerformerImageResolver? performerImages = null)
        => Diff(
            catalogue,
            BuildOwnedIdSet(ownedVideos, idFamily),
            exclusions,
            entityName,
            idFamily,
            statusIndex,
            performerImages);

    /// <summary>
    /// The same diff over a pre-resolved owned-id set rather than the owned videos themselves.
    /// </summary>
    /// <remarks>
    /// The subtraction only ever asks whether an id ON THE CATALOGUE is owned, so a caller whose owned set would
    /// otherwise be the whole library resolves just those ids and passes the answer. Nothing about the result
    /// differs; the id-set overload is simply the one an unbounded owned set can be expressed through.
    /// </remarks>
    public static IReadOnlyList<MissingScene> Diff(
        IReadOnlyList<WhisparrMovie> catalogue,
        IReadOnlySet<string> ownedIds,
        IReadOnlyList<WhisparrExclusion> exclusions,
        string? entityName,
        DiscoveryIdFamily idFamily = DiscoveryIdFamily.StashDb,
        IReadOnlyDictionary<string, WhisparrMovie>? statusIndex = null,
        PerformerImageResolver? performerImages = null)
    {
        var owned = ownedIds;
        var excluded = SceneStatusProjector.BuildExcludedSet(exclusions);
        var performerImageResolver = performerImages ?? PerformerImageResolver.Empty;

        // The index a survivor's status is classified against — the Whisparr reconciliation movie set (STATUS,
        // never the catalogue): a direct catalogue row that is a monitored Whisparr movie reads wanted, an
        // unmonitored one unmonitored, and one Whisparr has no row for reads notAdded. Absence and emptiness are
        // different facts and stay apart. No index at all means the caller had no authority to classify against —
        // v2 builds no StashDB-keyed reconciliation index, and a v3 movie-set read can fail — which yields unknown
        // for every row; a present-but-empty index means Whisparr answered and holds no matching row, which is a
        // true notAdded.
        var statusIsAuthoritative = statusIndex is not null;
        var index = statusIndex ?? EmptyStatusIndex;

        var missing = new List<MissingScene>();
        foreach (var movie in catalogue)
        {
            var candidateIds = CandidateIds(movie, idFamily);
            if (candidateIds.Count == 0)
            {
                // No stable source id to diff on: without an id it cannot be asserted owned/excluded, so it is
                // NOT surfaced as missing — listing an unidentifiable row risks showing a duplicate of an owned
                // scene the id match would otherwise subtract.
                continue;
            }

            var ownedOrExcluded = false;
            foreach (var id in candidateIds)
            {
                if (owned.Contains(id) || excluded.Contains(id))
                {
                    ownedOrExcluded = true;
                    break;
                }
            }

            if (!ownedOrExcluded)
            {
                missing.Add(Project(
                    movie, candidateIds, entityName, index, statusIsAuthoritative, excluded, performerImageResolver));
            }
        }

        return missing;
    }

    /// <summary>
    /// Every id, in <paramref name="idFamily"/>, that any row of <paramref name="catalogue"/> could be subtracted
    /// by — the exact question set a caller resolves an owned answer for.
    /// </summary>
    /// <remarks>
    /// Derived from the same <c>CandidateIds</c> rule the diff itself applies, so a caller cannot resolve a
    /// narrower set than the diff will go on to ask about and silently under-subtract.
    /// </remarks>
    public static IReadOnlyCollection<string> CatalogueCandidateIds(
        IReadOnlyList<WhisparrMovie> catalogue, DiscoveryIdFamily idFamily)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var movie in catalogue)
        {
            foreach (var id in CandidateIds(movie, idFamily))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    // The owned subtrahend: every id (in the connected version's family) on the entity's own Cove videos — the
    // v3 StashDB key (Cove StashDB UUID ↔ Whisparr scene stashId) or the v2 TPDB key (Cove TPDB id ↔ Whisparr v2
    // episode id), the SAME key SceneStatusProjector matches on. Keying on the WRONG family would fail to
    // subtract owned scenes and mis-report them as missing, so the family follows the connected version.
    internal static HashSet<string> BuildOwnedIdSet(IReadOnlyList<CoveVideo> ownedVideos, DiscoveryIdFamily idFamily)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var video in ownedVideos)
        {
            var ids = idFamily == DiscoveryIdFamily.Tpdb ? video.TpdbIds : video.StashIds;
            foreach (var id in ids)
            {
                if (!string.IsNullOrEmpty(id))
                {
                    set.Add(id);
                }
            }
        }

        return set;
    }

    // The catalogue movie's diff-comparable ids in the connected version's family. On v3 (StashDB), mirror
    // SceneStatusProjector.BuildMovieIndex keying: the movie's stashId, plus its foreignId ONLY for a scene-typed
    // row (a movie-typed foreignId is a tmdbId, never a StashDB UUID — the field-polymorphism rule documented on
    // WhisparrMovie). On v2 (TPDB), a synthesized episode carries no StashDB id at all; its TPDB scene id lives in
    // ForeignId, which is the only id a v2 owned scene can be subtracted by.
    private static List<string> CandidateIds(WhisparrMovie movie, DiscoveryIdFamily idFamily)
    {
        if (idFamily == DiscoveryIdFamily.Tpdb)
        {
            return string.IsNullOrEmpty(movie.ForeignId) ? [] : [movie.ForeignId];
        }

        var ids = new List<string>(2);
        if (!string.IsNullOrEmpty(movie.StashId))
        {
            ids.Add(movie.StashId);
        }

        if (string.Equals(movie.ItemType, "scene", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(movie.ForeignId))
        {
            ids.Add(movie.ForeignId);
        }

        return ids;
    }

    private static MissingScene Project(
        WhisparrMovie movie,
        List<string> candidateIds,
        string? entityName,
        IReadOnlyDictionary<string, WhisparrMovie> statusIndex,
        bool statusIsAuthoritative,
        IReadOnlySet<string> excludedSet,
        PerformerImageResolver performerImages)
        => new(
            SourceId: candidateIds[0],
            Title: movie.Title,
            ReleaseDate: movie.ReleaseDate,
            EntityName: entityName,
            PosterUrl: PosterUrl(movie),
            CoverUrl: CoverUrl(movie),
            StudioName: string.IsNullOrEmpty(movie.StudioTitle) ? null : movie.StudioTitle,
            Performers: Performers(movie, performerImages),
            Tags: Facet(movie.TagNames),
            Overview: string.IsNullOrEmpty(movie.Overview) ? null : movie.Overview,
            Status: StatusOf(candidateIds, statusIndex, statusIsAuthoritative, excludedSet));

    // The scene's performers (name + avatar), from the index-aligned performerNames / performerForeignIds /
    // performerImageUrls arrays. Null when the row names none, so the card omits the chip strip. The avatar url is
    // the direct provider's own image (StashDB/TPDB carries one); the resolver is a fallback for a row that names
    // a performer but carries no image (matched by the movie's source performer id, then by name). Null when
    // neither source has an image, so the chip renders its placeholder glyph.
    private static MissingPerformer[]? Performers(WhisparrMovie movie, PerformerImageResolver performerImages)
    {
        if (movie.PerformerNames is not { Length: > 0 } names)
        {
            return null;
        }

        var images = movie.PerformerImageUrls;
        var foreignIds = movie.PerformerForeignIds;
        return [.. names.Select((name, i) =>
        {
            var directUrl = images is not null && i < images.Length ? images[i] : null;
            if (!string.IsNullOrEmpty(directUrl))
            {
                return new MissingPerformer(name, directUrl);
            }

            var stashId = foreignIds is not null && i < foreignIds.Length ? foreignIds[i] : null;
            var coveUrl = performerImages.Resolve(stashId, name);
            return new MissingPerformer(name, string.IsNullOrEmpty(coveUrl) ? null : coveUrl);
        })];
    }

    private static string[]? Facet(string[]? names)
        => names is { Length: > 0 } ? names : null;

    // The decision is AUTHORITY, never the index's size: without an index there is no evidence about any scene,
    // and a verdict drawn from its absence would report a Whisparr outage as a fact about the scene. With
    // authority it reuses the shared classifier so preview and status agree on identity; Excluded is unreachable
    // there (an excluded id was already subtracted above), which is why the four-state result collapses to three.
    private static string StatusOf(
        IReadOnlyList<string> candidateIds,
        IReadOnlyDictionary<string, WhisparrMovie> statusIndex,
        bool statusIsAuthoritative,
        IReadOnlySet<string> excludedSet)
        => !statusIsAuthoritative
            ? DiscoveryMissingStatus.Unknown
            : SceneStatusProjector.Classify(candidateIds, statusIndex, excludedSet) switch
            {
                SceneWhisparrState.Monitored => DiscoveryMissingStatus.Wanted,
                SceneWhisparrState.Unmonitored => DiscoveryMissingStatus.Unmonitored,
                _ => DiscoveryMissingStatus.NotAdded,
            };

    // The poster image url: the images entry whose coverType is the poster kind, preferring the source-served
    // remoteUrl over the host-relative cached url. Null when the row carries no poster (the UI renders a
    // fallback tile) — a scene often carries only screenshots, so absence is expected, not an error.
    private static string? PosterUrl(WhisparrMovie movie)
        => PickImage(movie, "poster");

    // The landscape cover the wide card media box renders: a scene most often carries a screenshot, so that is
    // preferred, then a fanart/banner landscape kind, and only then the portrait poster as a last resort. Null
    // when the row carries no image at all (the card renders a fallback tile). Preferring a landscape kind keeps
    // the 16:9 media box from letterboxing a portrait poster.
    private static string? CoverUrl(WhisparrMovie movie)
        => PickImage(movie, "screenshot")
            ?? PickImage(movie, "fanart")
            ?? PickImage(movie, "banner")
            ?? PosterUrl(movie);

    // The url of the first image of the requested coverType, preferring the source-served remoteUrl over the
    // host-relative cached url; null when the row carries no image of that kind.
    private static string? PickImage(WhisparrMovie movie, string coverType)
    {
        if (movie.Images is null)
        {
            return null;
        }

        foreach (var image in movie.Images)
        {
            if (string.Equals(image.CoverType, coverType, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrEmpty(image.RemoteUrl) ? image.Url : image.RemoteUrl;
            }
        }

        return null;
    }
}

/// <summary>
/// A pure source-id / name → Cove performer avatar-url lookup the diff consults when a catalogue row names
/// performers but carries no source-served image. A direct catalogue row carries its own avatars, so in
/// production this resolver is empty; it stays a supported fallback shape and is a plain dictionary lookup, so
/// the diff stays structurally I/O-free.
/// </summary>
internal sealed class PerformerImageResolver
{
    public static readonly PerformerImageResolver Empty = new(
        new Dictionary<string, string>(0), new Dictionary<string, string>(0));

    private readonly IReadOnlyDictionary<string, string> _byStashId;
    private readonly IReadOnlyDictionary<string, string> _byName;

    public PerformerImageResolver(
        IReadOnlyDictionary<string, string> byStashId, IReadOnlyDictionary<string, string> byName)
    {
        _byStashId = byStashId;
        _byName = byName;
    }

    // The StashDB id is a globally-unique key, so it is preferred; the name is the fallback when a row carries no
    // id (or Cove stored the performer without that id). Null when Cove has an avatar for neither.
    public string? Resolve(string? stashId, string name)
    {
        if (!string.IsNullOrEmpty(stashId) && _byStashId.TryGetValue(stashId, out var byId))
        {
            return byId;
        }

        return _byName.TryGetValue(NormalizeName(name), out var byName) ? byName : null;
    }

    public static string NormalizeName(string name) => name.Trim().ToLowerInvariant();
}
