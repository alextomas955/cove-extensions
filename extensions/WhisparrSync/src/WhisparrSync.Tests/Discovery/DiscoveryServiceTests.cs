using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Discovery;
using WhisparrSync.Library;
using WhisparrSync.SceneStatus;

namespace WhisparrSync.Tests.Discovery;

/// <summary>
/// The pure discovery-diff contract: <c>catalogue − Cove-owned(by id) − excluded</c>. Every test drives
/// <see cref="DiscoveryService"/> with plain in-memory collections (a catalogue array, owned Cove videos, an
/// exclusion array) — the service takes no client/DB/store handle, so "makes no metadata call" is STRUCTURAL
/// here, not mocked. Proves the load-bearing rule (owned-by-id beats a fileless Whisparr row), the exclusion
/// drop, the empty-catalogue no-throw, that per-scene status projects from the reconciliation index onto a
/// direct catalogue, and that a caller supplying no index at all gets an abstention.
/// </summary>
[Trait("Tier", "L0")]
public sealed class DiscoveryServiceTests
{
    private static WhisparrMovie Movie(
        int id, string? stashId, bool hasFile, string? title = null,
        string? itemType = "movie", string? foreignId = null,
        string? releaseDate = null, WhisparrImage[]? images = null,
        bool monitored = true, string[]? performerNames = null, string[]? tagNames = null,
        string[]? performerImageUrls = null, string? overview = null, string[]? performerForeignIds = null,
        string? studioTitle = null)
        => new(
            Id: id,
            Title: title ?? $"Scene {id}",
            Year: 2021,
            StashId: stashId,
            ForeignId: foreignId,
            ItemType: itemType,
            Monitored: monitored,
            HasFile: hasFile,
            MovieFile: null,
            StudioTitle: studioTitle,
            PerformerForeignIds: performerForeignIds,
            ReleaseDate: releaseDate,
            Images: images,
            PerformerNames: performerNames,
            TagNames: tagNames,
            Overview: overview,
            PerformerImageUrls: performerImageUrls);

    // A v2 catalogue row: a synthesized episode — no StashDB id, the TPDB scene id in ForeignId, the "v2scene"
    // marker — matching V2Adapter.SynthesizeEpisodes.
    private static WhisparrMovie V2Scene(int id, string tpdbId, bool hasFile, string? title = null)
        => new(
            Id: id,
            Title: title ?? $"Episode {id}",
            Year: 2021,
            StashId: null,
            ForeignId: tpdbId,
            ItemType: "v2scene",
            Monitored: true,
            HasFile: hasFile,
            MovieFile: null,
            StudioTitle: "Some Site");

    private static CoveVideo Video(int id, params string[] stashIds)
        => new(id, $"Video {id}", new DateOnly(2021, 1, 1), stashIds, [], [], []);

    private static CoveVideo VideoTpdb(int id, params string[] tpdbIds)
        => new(id, $"Video {id}", new DateOnly(2021, 1, 1), [], tpdbIds, [], []);

    private static WhisparrExclusion Exclusion(string foreignId)
        => new(1, foreignId, "Excluded", 2021);

    [Fact]
    public void Diff_returns_catalogue_scenes_not_owned_and_not_excluded()
    {
        var catalogue = new[]
        {
            Movie(1, "stash-a", hasFile: false),
            Movie(2, "stash-b", hasFile: true),
            Movie(3, "stash-c", hasFile: false),
        };
        var owned = new[] { Video(10, "stash-b") };

        var missing = DiscoveryService.Diff(catalogue, owned, [], "Some Studio");

        Assert.Equal(new[] { "stash-a", "stash-c" }, missing.Select(m => m.SourceId).OrderBy(s => s));
    }

    [Fact]
    public void Diff_owned_by_id_beats_hasFile_false()
    {
        // The load-bearing case: an owned scene whose file lives outside a Whisparr root reads hasFile:false,
        // yet MUST NOT be listed as missing. The diff keys on the source id, never hasFile.
        var catalogue = new[] { Movie(1, "stash-owned", hasFile: false) };
        var owned = new[] { Video(10, "stash-owned") };

        var missing = DiscoveryService.Diff(catalogue, owned, [], "Some Studio");

        Assert.Empty(missing);
    }

    [Fact]
    public void Diff_drops_excluded_ids()
    {
        var catalogue = new[]
        {
            Movie(1, "stash-a", hasFile: false),
            Movie(2, "stash-excluded", hasFile: false),
        };

        var missing = DiscoveryService.Diff(catalogue, [], [Exclusion("stash-excluded")], "Some Studio");

        Assert.Equal(["stash-a"], missing.Select(m => m.SourceId));
    }

    [Fact]
    public void Diff_matches_scene_row_foreignId_when_stashId_absent()
    {
        // A scene-typed row carries its StashDB id in foreignId (not stashId); an owned scene keyed on that id
        // must still subtract, mirroring SceneStatusProjector.BuildMovieIndex.
        var catalogue = new[] { Movie(1, stashId: null, hasFile: false, itemType: "scene", foreignId: "stash-scene") };
        var owned = new[] { Video(10, "stash-scene") };

        var missing = DiscoveryService.Diff(catalogue, owned, [], "Some Studio");

        Assert.Empty(missing);
    }

    [Fact]
    public void Diff_empty_catalogue_is_empty_not_a_throw()
    {
        var missing = DiscoveryService.Diff([], [Video(1, "stash-x")], [], "Some Studio");

        Assert.Empty(missing);
    }

    [Fact]
    public void Diff_projects_display_facts_and_stamps_entity_name()
    {
        var catalogue = new[]
        {
            Movie(
                1, "stash-a", hasFile: false, title: "A Scene", releaseDate: "2021-03-03",
                images:
                [
                    new WhisparrImage("screenshot", "/cover/1.jpg", "https://src/1.jpg"),
                    new WhisparrImage("poster", "/cover/p.jpg", "https://src/poster.jpg"),
                ]),
        };

        var missing = DiscoveryService.Diff(catalogue, [], [], "My Studio");

        var row = Assert.Single(missing);
        Assert.Equal("stash-a", row.SourceId);
        Assert.Equal("A Scene", row.Title);
        Assert.Equal("2021-03-03", row.ReleaseDate);
        Assert.Equal("My Studio", row.EntityName);
        // Poster prefers the source-served remoteUrl of the poster-coverType entry, not the screenshot.
        Assert.Equal("https://src/poster.jpg", row.PosterUrl);
    }

    [Fact]
    public void Diff_absent_poster_yields_null_poster_url()
    {
        var catalogue = new[]
        {
            Movie(1, "stash-a", hasFile: false, images: [new WhisparrImage("screenshot", "/s.jpg", "https://src/s.jpg")]),
        };

        var row = Assert.Single(DiscoveryService.Diff(catalogue, [], [], "My Studio"));
        Assert.Null(row.PosterUrl);
    }

    [Fact]
    public void Diff_v2_subtracts_owned_by_tpdb_id()
    {
        // A v2 scene carries no StashDB id — its only diff key is the TPDB id in ForeignId. Under the Tpdb id
        // family the diff subtracts owned scenes by their TPDB id, exactly as v3 subtracts by StashDB id.
        var catalogue = new[]
        {
            V2Scene(1, "tpdb-1", hasFile: false),
            V2Scene(2, "tpdb-owned", hasFile: true),
            V2Scene(3, "tpdb-3", hasFile: false),
        };
        var owned = new[] { VideoTpdb(10, "tpdb-owned") };

        var missing = DiscoveryService.Diff(catalogue, owned, [], "Some Site", DiscoveryIdFamily.Tpdb);

        Assert.Equal(new[] { "tpdb-1", "tpdb-3" }, missing.Select(m => m.SourceId).OrderBy(s => s));
    }

    [Fact]
    public void Diff_v2_keys_only_on_tpdb_never_a_stashdb_owned_id()
    {
        // A Cove video that carries an id in the StashDB family (but none in TPDB) must NOT subtract a
        // v2 catalogue row — the families are distinct, so keying on the wrong one would mis-report an owned
        // scene as missing. Here the owned video's StashDB id string coincidentally equals the catalogue's TPDB
        // id, yet under the Tpdb family it must not match.
        var catalogue = new[] { V2Scene(1, "tpdb-1", hasFile: false) };
        var owned = new[] { Video(10, "tpdb-1") };

        var missing = DiscoveryService.Diff(catalogue, owned, [], "Some Site", DiscoveryIdFamily.Tpdb);

        Assert.Equal(["tpdb-1"], missing.Select(m => m.SourceId));
    }

    [Fact]
    public void Diff_performer_uses_the_same_stashdb_model_as_studio()
    {
        // A performer's missing list is the same catalogue − owned(by StashDB id) diff as a studio's — the pure
        // diff is entity-kind-agnostic (the performer-vs-studio split is an attribution concern upstream, in the
        // adapter). Proves performer parity through the shared projection.
        var catalogue = new[]
        {
            Movie(1, "stash-p1", hasFile: false, title: "Performer Scene 1"),
            Movie(2, "stash-owned", hasFile: false, title: "Performer Scene 2"),
        };
        var owned = new[] { Video(10, "stash-owned") };

        var missing = DiscoveryService.Diff(catalogue, owned, [], "A Performer", DiscoveryIdFamily.StashDb);

        var row = Assert.Single(missing);
        Assert.Equal("stash-p1", row.SourceId);
        Assert.Equal("A Performer", row.EntityName);
    }

    [Fact]
    public void Diff_direct_path_projects_facets_and_notAdded_status()
    {
        // The direct-provider row carries performer/tag names and is a synthesized entry (not a real added
        // movie). Its subject is the projection; the index is AUTHORITATIVE but empty — Whisparr answered
        // and holds no matching row — under which every direct row reads notAdded, never a false unmonitored, and
        // the facets flow onto the projection for the card chips.
        var catalogue = new[]
        {
            Movie(
                1, "stash-a", hasFile: false, performerNames: ["Performer One", "Performer Two"],
                performerImageUrls: ["https://img/1.jpg", ""], tagNames: ["Tag Alpha"], overview: "A blurb"),
        };

        var row = Assert.Single(DiscoveryService.Diff(
            catalogue, [], [], "My Studio", DiscoveryIdFamily.StashDb, SceneStatusProjector.BuildMovieIndex([])));
        Assert.Equal(new[] { "Performer One", "Performer Two" }, row.Performers!.Select(p => p.Name));
        // The index-aligned avatar url carries through; an empty slot maps to null (the chip placeholder).
        Assert.Equal("https://img/1.jpg", row.Performers![0].ImageUrl);
        Assert.Null(row.Performers![1].ImageUrl);
        Assert.Equal(new[] { "Tag Alpha" }, row.Tags);
        Assert.Equal("A blurb", row.Overview);
        Assert.Equal(DiscoveryMissingStatus.NotAdded, row.Status);
    }

    [Fact]
    public void Diff_projects_status_from_the_reconciliation_index_onto_a_direct_catalogue()
    {
        // The STATUS-from-index-onto-a-direct-catalogue invariant: a direct catalogue row is a synthesized entry
        // (Id 0) whose status must NOT be read from the row itself. It comes from a SEPARATE reconciliation movie
        // index (Whisparr for STATUS, never for the catalogue). The three situations a single empty dictionary
        // cannot tell apart are pinned here side by side, which is what keeps any one of them from collapsing
        // quietly into another: a populated index classifies per row, an authoritative empty index is a true
        // notAdded, and no index at all is an abstention.
        var direct = new[]
        {
            Movie(0, "stash-mon", hasFile: false),
            Movie(0, "stash-unmon", hasFile: false),
        };
        var reconciliation = SceneStatusProjector.BuildMovieIndex(
        [
            Movie(1, "stash-mon", hasFile: false, monitored: true),
            Movie(2, "stash-unmon", hasFile: false, monitored: false),
        ]);

        var withIndex = DiscoveryService.Diff(direct, [], [], "My Studio", DiscoveryIdFamily.StashDb, reconciliation);
        Assert.Equal(DiscoveryMissingStatus.Wanted, withIndex.Single(m => m.SourceId == "stash-mon").Status);
        Assert.Equal(DiscoveryMissingStatus.Unmonitored, withIndex.Single(m => m.SourceId == "stash-unmon").Status);

        // An AUTHORITATIVE but empty index: Whisparr answered and holds no row for either scene — the one
        // situation where notAdded is a true claim about the scene.
        var authoritativeEmpty = DiscoveryService.Diff(
            direct, [], [], "My Studio", DiscoveryIdFamily.StashDb, SceneStatusProjector.BuildMovieIndex([]));
        Assert.All(authoritativeEmpty, m => Assert.Equal(DiscoveryMissingStatus.NotAdded, m.Status));

        // NO index: the v2 path, or a v3 status-index read that did not answer. Neither carries evidence about a
        // scene, and the catalogue still renders.
        var noIndex = DiscoveryService.Diff(direct, [], [], "My Studio", DiscoveryIdFamily.StashDb);
        Assert.All(noIndex, m => Assert.Equal(DiscoveryMissingStatus.Unknown, m.Status));
    }

    [Fact]
    public void Diff_with_no_status_index_abstains_rather_than_asserting_not_added()
    {
        // The older generation builds no StashDB-keyed reconciliation index, and a v3 movie-set read can fail.
        // Neither may be dressed as "Whisparr holds no row for this scene" — a claim about the acquisition side
        // that the diff has nothing to base on.
        var v3Direct = new[] { Movie(0, "stash-a", hasFile: false), Movie(0, "stash-b", hasFile: false) };
        var v2Direct = new[] { V2Scene(0, "tpdb-a", hasFile: false), V2Scene(0, "tpdb-b", hasFile: false) };

        var v3Rows = DiscoveryService.Diff(v3Direct, [], [], "My Studio", DiscoveryIdFamily.StashDb);
        var v2Rows = DiscoveryService.Diff(v2Direct, [], [], "Some Site", DiscoveryIdFamily.Tpdb);

        Assert.All(v3Rows, m => Assert.Equal(DiscoveryMissingStatus.Unknown, m.Status));
        Assert.All(v2Rows, m => Assert.Equal(DiscoveryMissingStatus.Unknown, m.Status));
        Assert.DoesNotContain(DiscoveryMissingStatus.NotAdded, v3Rows.Concat(v2Rows).Select(m => m.Status));
    }

    [Fact]
    public void Diff_resolves_performer_avatars_from_the_resolver()
    {
        // A catalogue row can name performers (performerNames + performerForeignIds) yet carry no performer image;
        // the resolver is the fallback that maps each chip to a Cove avatar: by source id first, then by
        // normalized name; neither → null (the chip's glyph). A direct image, when present, wins over the resolver.
        var catalogue = new[]
        {
            Movie(
                1, "stash-a", hasFile: false,
                performerNames: ["By Id", "By Name", "Unknown", "Has Direct"],
                performerForeignIds: ["perf-id-1", "no-cove-id", "no-cove-id-2", "perf-id-4"],
                performerImageUrls: [null!, null!, null!, "https://src/direct.jpg"]),
        };
        var resolver = new PerformerImageResolver(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["perf-id-1"] = "/api/performers/11/image?max=64",
                ["perf-id-4"] = "/api/performers/44/image?max=64",
            },
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [PerformerImageResolver.NormalizeName("By Name")] = "/api/performers/22/image?max=64",
            });

        var row = Assert.Single(
            DiscoveryService.Diff(catalogue, [], [], "My Studio", DiscoveryIdFamily.StashDb, null, resolver));

        Assert.Equal("/api/performers/11/image?max=64", row.Performers![0].ImageUrl); // by StashDB id
        Assert.Equal("/api/performers/22/image?max=64", row.Performers![1].ImageUrl); // by normalized name
        Assert.Null(row.Performers![2].ImageUrl); // neither → glyph
        Assert.Equal("https://src/direct.jpg", row.Performers![3].ImageUrl); // direct image wins over the resolver
    }

    // ---- The two structural properties an upstream-ordered, upstream-filtered read rests on ----

    // A catalogue whose row order matches NEITHER its release dates NOR its titles. An implementation that
    // sorted its survivors by either is caught by that mismatch alone. Each row also carries the facet values the
    // commuting property is exercised over.
    private static WhisparrMovie[] ScrambledCatalogue()
        =>
        [
            Movie(1, "id-m", hasFile: false, title: "Midway", releaseDate: "2019-06-01",
                studioTitle: "Alpha Studio", performerNames: ["Ada"], tagNames: ["outdoor"]),
            Movie(2, "id-a", hasFile: false, title: "Ablaze", releaseDate: "2023-01-15",
                studioTitle: "Beta Studio", performerNames: ["Bo"], tagNames: ["indoor"]),
            Movie(3, "id-z", hasFile: false, title: "Zenith", releaseDate: "2017-11-30",
                studioTitle: "Alpha Studio", performerNames: ["Ada", "Bo"], tagNames: ["outdoor", "indoor"]),
            Movie(4, "id-c", hasFile: false, title: "Cascade", releaseDate: "2021-04-04",
                studioTitle: "Beta Studio", performerNames: ["Cy"], tagNames: ["indoor"]),
            Movie(5, "id-b", hasFile: false, title: "Bright", releaseDate: "2015-02-20",
                studioTitle: "Alpha Studio", performerNames: ["Ada"], tagNames: []),
        ];

    private static string[] Ids(IEnumerable<MissingScene> missing) => [.. missing.Select(m => m.SourceId)];

    [Fact]
    public void Diff_preserves_catalogue_encounter_order()
    {
        // The property every provider-side ORDERING rests on: the diff appends survivors in catalogue encounter
        // order. The missing rows of an already-ordered page are therefore that page's survivors in the same
        // order. Asserted on the full ORDERED id sequence, never on a set — a set comparison passes just as
        // happily on an implementation that sorts its survivors, which is the one failure this test must catch.
        var catalogue = ScrambledCatalogue();
        var owned = new[] { Video(10, "id-z"), Video(11, "id-b") };

        var missing = DiscoveryService.Diff(catalogue, owned, [], "Some Studio");

        Assert.Equal(new[] { "id-m", "id-a", "id-c" }, Ids(missing));

        // The same catalogue reversed yields the reversed survivor sequence. A single-order case cannot tell an
        // order that came from the INPUT apart from one the diff happened to impose; two opposite orders can.
        var reversed = catalogue.Reverse().ToArray();

        var reversedMissing = DiscoveryService.Diff(reversed, owned, [], "Some Studio");

        Assert.Equal(new[] { "id-c", "id-a", "id-m" }, Ids(reversedMissing));
    }

    // Row-level predicates over the values BOTH providers filter on — a studio title, a performer name, a tag,
    // and the leading year of a release date. Local to the test: a single caller earns no production abstraction.
    public static TheoryData<string> FacetPredicateNames()
    {
        var data = new TheoryData<string>();
        foreach (var name in new[] { "studio", "performer", "tag", "year", "nothing", "everything" })
        {
            data.Add(name);
        }

        return data;
    }

    private static Func<WhisparrMovie, bool> FacetPredicate(string name)
        => name switch
        {
            "studio" => m => m.StudioTitle == "Alpha Studio",
            "performer" => m => m.PerformerNames is { } names && names.Contains("Ada"),
            "tag" => m => m.TagNames is { } tags && tags.Contains("outdoor"),
            "year" => m => m.ReleaseDate is { } date && date.StartsWith("202", StringComparison.Ordinal),
            "nothing" => _ => false,
            "everything" => _ => true,
            _ => throw new ArgumentOutOfRangeException(nameof(name), name, "no predicate defined"),
        };

    [Theory]
    [MemberData(nameof(FacetPredicateNames))]
    public void Diff_commutes_with_a_row_level_facet_predicate(string predicateName)
    {
        // The property every provider-side FILTER rests on: no survivor's fate depends on any OTHER row, which
        // makes filtering the catalogue before the diff and filtering the result after it produce the same rows
        // in the same order. That equality is what makes a provider-applied facet exact; without it the rows a
        // provider filter returns would only approximate the ones the client-side predicate would have shown.
        // A cap, a de-duplication or a group-by over the survivors is what breaks it. Compared as ordered
        // SEQUENCES, for the same reason the order property is.
        var predicate = FacetPredicate(predicateName);
        var catalogue = ScrambledCatalogue();
        var owned = new[] { Video(10, "id-z") };
        var exclusions = new[] { Exclusion("id-b") };

        var filteredThenDiffed = DiscoveryService.Diff(
            [.. catalogue.Where(predicate)], owned, exclusions, "Some Studio");
        var diffedThenFiltered = DiscoveryService.Diff(catalogue, owned, exclusions, "Some Studio")
            .Where(row => predicate(catalogue.Single(m => m.StashId == row.SourceId)));

        Assert.Equal(Ids(diffedThenFiltered), Ids(filteredThenDiffed));
    }

    [Fact]
    public void Diff_still_subtracts_an_owned_scene_whose_whisparr_row_has_no_file()
    {
        // The ownership rule re-proven in the SHAPE a provider-side read introduces, not only the shape it
        // shipped under: an oldest-first catalogue that has already been narrowed by a facet. An owned scene whose
        // file lives outside a Whisparr root reads hasFile:false and is still emphatically not missing.
        var oldestFirst = ScrambledCatalogue()
            .OrderBy(m => m.ReleaseDate, StringComparer.Ordinal)
            .Where(FacetPredicate("studio"))
            .ToArray();
        var owned = new[] { Video(10, "id-z") };

        var missing = DiscoveryService.Diff(oldestFirst, owned, [], "Some Studio");

        Assert.All(oldestFirst, m => Assert.False(m.HasFile));
        Assert.DoesNotContain("id-z", Ids(missing));
        // The ordering the caller supplied still comes back untouched: id-b (2015) before id-m (2019).
        Assert.Equal(new[] { "id-b", "id-m" }, Ids(missing));
    }

    [Fact]
    public void Diff_subtracts_on_the_connected_id_family_under_a_filtered_catalogue()
    {
        // A filter narrows WHICH ROWS are considered; it never changes WHAT IS SUBTRACTED. The same filtered
        // catalogue diffed under the two id families drops different rows, because each family keys on the id its
        // own generation carries. A facet filter therefore can never be mistaken for the ownership subtraction.
        var catalogue = new[]
        {
            Movie(1, "stash-a", hasFile: false, foreignId: "tpdb-a", studioTitle: "Alpha Studio"),
            Movie(2, "stash-b", hasFile: false, foreignId: "tpdb-b", studioTitle: "Alpha Studio"),
            Movie(3, "stash-c", hasFile: false, foreignId: "tpdb-c", studioTitle: "Beta Studio"),
        };
        var filtered = catalogue.Where(FacetPredicate("studio")).ToArray();
        var owned = new[] { Video(10, "stash-a"), VideoTpdb(11, "tpdb-b") };

        var stashDb = DiscoveryService.Diff(filtered, owned, [], "Alpha Studio", DiscoveryIdFamily.StashDb);
        var tpdb = DiscoveryService.Diff(filtered, owned, [], "Alpha Studio", DiscoveryIdFamily.Tpdb);

        // Under StashDb the video carrying the StashDB id subtracts; under Tpdb the one carrying the TPDB id does.
        Assert.Equal(new[] { "stash-b" }, Ids(stashDb));
        Assert.Equal(new[] { "tpdb-a" }, Ids(tpdb));
    }

    // ---- The server-side routing decision (pure) ----

    [Fact]
    public void Router_no_remote_id_is_noSourceId_regardless_of_the_rest()
    {
        // No resolvable id in the connected version's family → nothing to enumerate: the honest noSourceId, never
        // an empty own-everything (the id is missing, so we never even read a catalogue).
        Assert.Equal(
            DiscoveryRoute.NoSourceId,
            DiscoveryRouter.Decide(hasRemoteId: false, hasDirectKey: true));
    }

    [Fact]
    public void Router_with_a_credential_routes_direct()
    {
        // A resolved Cove metadata credential is the direct read — the sole catalogue source.
        Assert.Equal(
            DiscoveryRoute.Direct,
            DiscoveryRouter.Decide(hasRemoteId: true, hasDirectKey: true));
    }

    [Fact]
    public void Router_without_a_credential_is_needsProviderKey()
    {
        // The anti-empty-list rule: a remote id but no matching Cove metadata credential is a distinct actionable
        // "set up a metadata source in Cove" state, NOT an empty list that reads as own-everything, and NEVER a
        // thin through-Whisparr fallback.
        Assert.Equal(
            DiscoveryRoute.NeedsProviderKey,
            DiscoveryRouter.Decide(hasRemoteId: true, hasDirectKey: false));
    }
}
