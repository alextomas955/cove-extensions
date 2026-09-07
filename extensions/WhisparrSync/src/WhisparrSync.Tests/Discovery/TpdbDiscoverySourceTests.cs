using System.Text.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Discovery;
using WhisparrSync.Library;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Discovery;

/// <summary>
/// The offline drift-lock for the hand-rolled ThePornDB REST provider (the codegen substitute) — every
/// test is host-free and drives the transport with a <see cref="FakeHttpMessageHandler"/> primed from a captured,
/// content-safe <c>/scenes?site_id=</c> fixture (SFW allowlisted site, fabricated ids, no token). ThePornDB's
/// stash-box GraphQL <c>queryScenes</c> is gated (verified live), so the v2 catalogue is read from the REST
/// API; this pins the REST DTO shape (a field rename fails here), the scene→<c>WhisparrMovie</c> mapping the
/// TPDB-id diff keys on, the capability flags, and that only a read GET is issued (no mutation).
/// </summary>
[Trait("Tier", "L0")]
public sealed class TpdbDiscoverySourceTests
{
    // The numeric ThePornDB site id a v2 studio resolves to (SFW allowlisted brand; the fixture's site).
    private const string TushyRawSiteId = "247";

    // A canonical ThePornDB performer id is a UUID — the id Cove stores as the performer's ThePornDB remote id.
    private const string PerformerId = "3de6cd92-0000-4000-8000-000000000001";

    // A ThePornDB tag id is NUMERIC (its uuid is a secondary field), and the numeric one is what the filter keys on.
    private const string NumericTagId = "202";

    private static string Fixture()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestSupport", "fixtures", "tpdb-scenes-site.json"));

    private static string PerformerFixture()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestSupport", "fixtures", "tpdb-scenes-performer.json"));

    private static TpdbDiscoverySource ProviderFrom(FakeHttpMessageHandler handler)
        => new(new TpdbClient(new HttpClient(handler)), "https://api.theporndb.net", "test-token");

    [Fact]
    public void Fixture_deserializes_to_the_pinned_scenes_shape()
    {
        // The shape lock: the captured REST response binds into the hand-rolled DTO with the expected scene ids,
        // date, poster, site name, pagination, and the enriched facets (image, description, performers[].{face,
        // image, thumbnail}, tags[].name). A renamed/removed field breaks this.
        var response = JsonSerializer.Deserialize(Fixture(), TpdbJsonContext.Default.TpdbScenesResponse);

        Assert.Equal(3, response!.Data!.Length);
        var first = response.Data[0];
        Assert.Equal("7a1c0000-0000-4000-8000-000000000001", first.Id);
        Assert.Equal("2024-01-05", first.Date);
        Assert.Equal("https://cdn.example.test/poster/1.jpg", first.Poster);
        Assert.Equal("https://cdn.example.test/landscape/1.jpg", first.Image);
        Assert.Equal("A synthetic scene blurb used only to pin the overview mapping.", first.Description);
        Assert.Equal("Tushy Raw", first.Site!.Name);
        Assert.Equal(
            new[] { "Performer One", "Performer Two", "Performer Three", "Performer Four" },
            first.Performers!.Select(p => p.Name));
        // The three avatar candidate slots bind independently (face + image + thumbnail on the first
        // performer), and a performer carrying none binds every candidate null — the raw shape the
        // mapping's fallback picker then collapses.
        Assert.Equal("https://cdn.example.test/face/1.jpg", first.Performers![0].Face);
        Assert.Equal("https://cdn.example.test/image/1.jpg", first.Performers[0].Image);
        Assert.Equal("https://cdn.example.test/thumb/1.jpg", first.Performers[0].Thumbnail);
        Assert.Equal("https://cdn.example.test/image/2.jpg", first.Performers[1].Image);
        Assert.Equal("https://cdn.example.test/thumb/3.jpg", first.Performers[2].Thumbnail);
        Assert.Null(first.Performers[3].Face);
        Assert.Null(first.Performers[3].Image);
        Assert.Null(first.Performers[3].Thumbnail);
        Assert.Equal(new[] { "Tag Alpha", "Tag Beta" }, first.Tags!.Select(t => t.Name));
        Assert.Equal(1, response.Meta!.LastPage);
    }

    [Fact]
    public async Task EnumerateCatalogueAsync_maps_scenes_to_diff_ready_movies()
    {
        var provider = ProviderFrom(FakeHttpMessageHandler.Json(Fixture()));

        var result = await provider.EnumerateCatalogueAsync(EntityKind.Studio, [TushyRawSiteId], default);

        Assert.True(result.IsOk);
        var movies = result.Value!.Movies;
        Assert.Equal(3, movies.Length);

        var first = movies[0];
        // The ThePornDB scene UUID is in ForeignId (the TPDB id family the diff keys on), StashId stays null;
        // ItemType "scene"; the date + site name carry through.
        Assert.Equal("7a1c0000-0000-4000-8000-000000000001", first.ForeignId);
        Assert.Null(first.StashId);
        Assert.Equal("scene", first.ItemType);
        Assert.Equal("2024-01-05", first.ReleaseDate);
        Assert.Equal("Tushy Raw", first.StudioTitle);
        // ONE cover, from the POSTER — the only ThePornDB image field that loads cross-origin (its landscape
        // `image` points at the studio's own CDN and was measured 0/20 loading). The landscape field is not
        // offered at all, so no card can request a url that will fail.
        var cover = Assert.Single(first.Images!);
        Assert.Equal("screenshot", cover.CoverType);
        Assert.Equal("https://cdn.example.test/poster/1.jpg", cover.RemoteUrl);
        Assert.DoesNotContain(first.Images!, i => i.RemoteUrl!.Contains("landscape", StringComparison.Ordinal));
        // Performer names + index-aligned avatars picked in fallback order: face wins, then image, then
        // thumbnail, and "" when a performer carries none of the three (the slot is never dropped, keeping
        // names and urls aligned). The four performers exercise each rung of the chain in turn.
        Assert.Equal(
            new[] { "Performer One", "Performer Two", "Performer Three", "Performer Four" },
            first.PerformerNames);
        Assert.Equal(
            new[]
            {
                "https://cdn.example.test/face/1.jpg",
                "https://cdn.example.test/image/2.jpg",
                "https://cdn.example.test/thumb/3.jpg",
                "",
            },
            first.PerformerImageUrls);
        Assert.Equal(new[] { "Tag Alpha", "Tag Beta" }, first.TagNames);
        Assert.Equal("A synthetic scene blurb used only to pin the overview mapping.", first.Overview);

        // A scene carrying only a poster (with present-but-empty performers/tags arrays) still gets its cover,
        // and binds each rich facet null — the empty array collapses to null, never a zero-length array a
        // renderer would mistake for present.
        Assert.Equal("https://cdn.example.test/poster/2.jpg", Assert.Single(movies[1].Images!).RemoteUrl);
        Assert.Null(movies[1].PerformerNames);
        Assert.Null(movies[1].PerformerImageUrls);
        Assert.Null(movies[1].TagNames);
        Assert.Null(movies[1].Overview);

        // A scene with none of these binds every field null, never an empty array.
        Assert.Null(movies[2].Images);
        Assert.Null(movies[2].PerformerNames);
        Assert.Null(movies[2].TagNames);
        Assert.Null(movies[2].Overview);
    }

    [Fact]
    public async Task Mapped_movies_flow_through_DiscoveryService_diff_on_the_tpdb_family()
    {
        // The provider-agnostic promise: the mapped rows subtract by TPDB id in the pure diff with no
        // reshape. Owning scene 1 leaves scenes 2 and 3 missing.
        var provider = ProviderFrom(FakeHttpMessageHandler.Json(Fixture()));
        var catalogue = (await provider.EnumerateCatalogueAsync(EntityKind.Studio, [TushyRawSiteId], default)).Value!;
        var owned = new[]
        {
            new CoveVideo(10, "Owned", new DateOnly(2024, 1, 5),
                [], ["7a1c0000-0000-4000-8000-000000000001"], [], []),
        };

        var missing = DiscoveryService.Diff(catalogue.Movies, owned, [], "Tushy Raw", DiscoveryIdFamily.Tpdb);

        Assert.Equal(
            new[] { "7a1c0000-0000-4000-8000-000000000002", "7a1c0000-0000-4000-8000-000000000003" },
            missing.Select(m => m.SourceId).OrderBy(s => s));
    }

    [Fact]
    public void Provider_advertises_credential_and_unmonitored_capability()
    {
        var provider = ProviderFrom(FakeHttpMessageHandler.Json(Fixture()));

        Assert.True(provider.NeedsCredential);
        Assert.True(provider.CanEnumerateUnmonitored);
    }

    [Fact]
    public async Task EnumerateCatalogueAsync_issues_exactly_one_read_get_and_no_mutation()
    {
        // Read-only lock: one page (last_page=1) is a single GET carrying the site_id filter and the
        // Bearer token — never a POST/PUT/DELETE mutation, never a Whisparr call.
        var handler = FakeHttpMessageHandler.Json(Fixture());
        var provider = ProviderFrom(handler);

        await provider.EnumerateCatalogueAsync(EntityKind.Studio, [TushyRawSiteId], default);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Contains("site_id=247", handler.LastRequest.RequestUri!.Query, StringComparison.Ordinal);
        Assert.True(handler.LastRequest.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Performer_kind_reads_the_per_entity_scenes_path_with_the_rich_projection()
    {
        // The performer path is ThePornDB's per-entity sub-resource, NOT a /scenes filter — the canonical id is a
        // path segment. Rows map through the same rich projection as a site read.
        var handler = FakeHttpMessageHandler.Json(Fixture());
        var provider = ProviderFrom(handler);

        var result = await provider.EnumerateCatalogueAsync(EntityKind.Performer, [PerformerId], default);

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value!.Movies.Length);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal(
            $"/performers/{PerformerId}/scenes",
            handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("site_id", handler.LastRequest.RequestUri.Query, StringComparison.Ordinal);
        Assert.True(handler.LastRequest.Headers.Contains("Authorization"));

        var first = result.Value!.Movies[0];
        Assert.Equal("scene", first.ItemType);
        Assert.NotNull(first.ForeignId);
        Assert.Null(first.StashId);
        Assert.NotNull(first.PerformerNames);
        Assert.NotNull(first.PerformerImageUrls);
        Assert.NotNull(first.TagNames);
        Assert.NotNull(first.Overview);
        Assert.NotNull(first.Images);
    }

    [Fact]
    public async Task Performer_page_read_is_one_call_and_reports_no_more_on_a_short_page()
    {
        // The paged performer read is caller-driven: exactly one upstream GET, and a page shorter than perPage is
        // the end-of-catalogue signal (ThePornDB's advertised count is not trustworthy).
        var handler = FakeHttpMessageHandler.Json(Fixture());
        var provider = ProviderFrom(handler);

        var result = await provider.EnumerateCataloguePageAsync(
            EntityKind.Performer, [PerformerId], page: 1, perPage: 100, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value!.Movies.Length);
        Assert.False(result.Value.HasMore);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("per_page=100", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tag_kind_filters_the_scene_collection_by_its_numeric_id()
    {
        // ThePornDB has no per-tag scene sub-resource: a tag filters /scenes through a Laravel object-map param
        // whose KEY is the numeric tag id. Rows map through the same rich projection as the other kinds.
        var handler = FakeHttpMessageHandler.Json(Fixture());
        var provider = ProviderFrom(handler);

        var result = await provider.EnumerateCatalogueAsync(EntityKind.Tag, [NumericTagId], default);

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value!.Movies.Length);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/scenes", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains($"tags[{NumericTagId}]=", Uri.UnescapeDataString(handler.LastRequest.RequestUri.Query), StringComparison.Ordinal);
        Assert.True(handler.LastRequest.Headers.Contains("Authorization"));

        var first = result.Value!.Movies[0];
        Assert.Equal("scene", first.ItemType);
        Assert.NotNull(first.ForeignId);
        Assert.NotNull(first.PerformerNames);
        Assert.NotNull(first.TagNames);
        Assert.NotNull(first.Images);
    }

    [Fact]
    public async Task Tag_page_read_is_one_call_and_reports_no_more_on_a_short_page()
    {
        // A tag's catalogue can far exceed a site's, and the source caps its advertised total at 10,000. The paged
        // read therefore stays caller-driven and infers the end from a SHORT page.
        var handler = FakeHttpMessageHandler.Json(Fixture());
        var provider = ProviderFrom(handler);

        var result = await provider.EnumerateCataloguePageAsync(
            EntityKind.Tag, [NumericTagId], page: 1, perPage: 100, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value!.Movies.Length);
        Assert.False(result.Value.HasMore);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Page_reports_the_sources_own_total_not_the_size_of_the_fetched_page()
    {
        // The tab's count must be the CATALOGUE size, never the one page it fetched — a 10,000-scene tag reporting
        // "40" (the page size) is the bug this pins.
        var handler = FakeHttpMessageHandler.Json(
            """
            {"data":[{"id":"s1","title":"A","date":null,"poster":null,"site":null}],
             "meta":{"current_page":1,"last_page":18,"total":1772}}
            """);
        var provider = ProviderFrom(handler);

        var result = await provider.EnumerateCataloguePageAsync(
            EntityKind.Performer, [PerformerId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(1772, result.Value!.Total);
        Assert.False(result.Value.TotalIsAtLeast);
    }

    [Fact]
    public async Task A_saturated_total_is_flagged_as_a_lower_bound()
    {
        // ThePornDB stops counting at 10,000: a broader set reports exactly that. Presenting it as an exact size
        // would overstate what the source said.
        var handler = FakeHttpMessageHandler.Json(
            """
            {"data":[{"id":"s1","title":"A","date":null,"poster":null,"site":null}],
             "meta":{"current_page":1,"last_page":100,"total":10000}}
            """);
        var provider = ProviderFrom(handler);

        var result = await provider.EnumerateCataloguePageAsync(
            EntityKind.Tag, [NumericTagId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(10_000, result.Value!.Total);
        Assert.True(result.Value.TotalIsAtLeast);
    }

    // A /scenes 200 body with an explicit advertised total. The total is what a ThePornDB filter has to be proven
    // by, and it is therefore the parameter that varies between a baseline body and a filtered one.
    private static string BodyWith(int total, int rows, int lastPage = 1)
    {
        var scenes = string.Join(",", Enumerable.Range(0, rows).Select(i =>
            $"{{\"id\":\"s{i}\",\"title\":\"Scene {i}\",\"date\":\"2021-0{(i % 9) + 1}-01\","
            + "\"poster\":null,\"site\":{\"id\":247,\"name\":\"Tushy Raw\"}}"));
        return $"{{\"data\":[{scenes}],\"meta\":{{\"current_page\":1,\"last_page\":{lastPage},"
            + $"\"total\":{total}}}}}";
    }

    private static string UnescapedQuery(FakeHttpMessageHandler handler)
        => Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.Query);

    [Theory]
    [InlineData("site", "site_id=4520")]
    [InlineData("performer", "performers[289008]=1")]
    [InlineData("tag", "tags[29]=1")]
    [InlineData("year", "year=2019")]
    public async Task Each_filter_axis_is_proven_by_a_total_that_moved(string axis, string expectedFragment)
    {
        // ThePornDB accepts an unknown or malformed parameter with a 200 and ignores it — eleven plausible
        // parameters each left the total unchanged (verified live). A case asserting only the URL would therefore
        // pass on a parameter the provider does nothing with, which is this provider's dominant failure mode. So
        // each axis is driven as a PAIR through one handler: an unfiltered baseline, then the filtered read, with
        // the assertion being that the reported total MOVED.
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Respond(System.Net.HttpStatusCode.OK, "application/json", BodyWith(total: 805, rows: 3)),
            FakeHttpMessageHandler.Respond(System.Net.HttpStatusCode.OK, "application/json", BodyWith(total: 76, rows: 3)));
        var provider = ProviderFrom(handler);

        // The site axis is driven on a TAG page, where it is not the entity's own axis; the rest on a site page.
        var (kind, remoteId, query) = axis switch
        {
            "site" => (EntityKind.Tag, NumericTagId, DiscoveryQuery.Default with { StudioFilterId = "4520" }),
            "performer" => (EntityKind.Studio, TushyRawSiteId, DiscoveryQuery.Default with { PerformerFilterId = "289008" }),
            "tag" => (EntityKind.Studio, TushyRawSiteId, DiscoveryQuery.Default with { TagFilterId = "29" }),
            _ => (EntityKind.Studio, TushyRawSiteId, DiscoveryQuery.Default with { Year = 2019 }),
        };

        var baseline = await provider.EnumerateCataloguePageAsync(
            kind, [remoteId], page: 1, perPage: 40, DiscoveryQuery.Default, default);
        Assert.Equal(805, baseline.Value!.Total);
        Assert.DoesNotContain(expectedFragment, UnescapedQuery(handler), StringComparison.Ordinal);

        var filtered = await provider.EnumerateCataloguePageAsync(kind, [remoteId], page: 1, perPage: 40, query, default);

        Assert.Contains(expectedFragment, UnescapedQuery(handler), StringComparison.Ordinal);
        Assert.Equal(76, filtered.Value!.Total);
        Assert.NotEqual(baseline.Value.Total, filtered.Value.Total);
    }

    [Fact]
    public async Task The_performer_filter_carries_parent_underscore_id_and_none_of_its_three_siblings()
    {
        // A scene row exposes four performer identifiers and only parent._id filters; the other three each return
        // total: 0, which is indistinguishable from owning every scene. The fixture holds all four with distinct
        // values, and the URL must carry exactly one of them.
        var handler = FakeHttpMessageHandler.Json(PerformerFixture());

        await ProviderFrom(handler).EnumerateCataloguePageAsync(
            EntityKind.Performer, [PerformerId], page: 1, perPage: 40,
            DiscoveryQuery.Default with { Year = 2024 }, default);

        var query = UnescapedQuery(handler);
        Assert.Contains("performers[289008]=1", query, StringComparison.Ordinal);
        Assert.DoesNotContain("7bb135c5-0000-4000-8000-00000000aaaa", query, StringComparison.Ordinal);
        Assert.DoesNotContain("2361967", query, StringComparison.Ordinal);
        Assert.DoesNotContain(PerformerId, query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unfiltered_performer_read_keeps_the_sub_resource_and_a_filtered_one_leaves_it()
    {
        // The branch this design deliberately keeps, visible in a test and not only in a comment. The
        // sub-resource is cleanly date-descending and cannot be filtered; the collection route can be filtered
        // and has no ordering contract.
        var plain = FakeHttpMessageHandler.Json(PerformerFixture());
        await ProviderFrom(plain).EnumerateCataloguePageAsync(
            EntityKind.Performer, [PerformerId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        Assert.Equal(1, plain.CallCount);
        Assert.Equal($"/performers/{PerformerId}/scenes", plain.LastRequest!.RequestUri!.AbsolutePath);

        var filtered = FakeHttpMessageHandler.Json(PerformerFixture());
        await ProviderFrom(filtered).EnumerateCataloguePageAsync(
            EntityKind.Performer, [PerformerId], page: 1, perPage: 40,
            DiscoveryQuery.Default with { TagFilterId = "29" }, default);

        Assert.Equal("/scenes", filtered.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("performers[289008]=1", UnescapedQuery(filtered), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_performer_whose_numeric_id_cannot_be_resolved_falls_back_to_the_sub_resource()
    {
        // Without the canonical numeric id there is no entity key for the collection route, and a criterion
        // missing it would either widen past this performer or return a zero nobody can read. The fallback is an
        // unfiltered page of the RIGHT performer, which the caller narrows over the rows it loaded.
        var handler = FakeHttpMessageHandler.Json(
            """
            {"data":[{"id":"s1","title":"A","date":"2024-01-01","poster":null,"site":null,
              "performers":[{"name":"Someone Else","face":null,"image":null,"thumbnail":null,
                "parent":{"id":"11111111-0000-4000-8000-000000000099","_id":999}}]}],
             "meta":{"current_page":1,"last_page":1,"total":7}}
            """);

        var result = await ProviderFrom(handler).EnumerateCataloguePageAsync(
            EntityKind.Performer, [PerformerId], page: 1, perPage: 40,
            DiscoveryQuery.Default with { Year = 2024 }, default);

        Assert.True(result.IsOk);
        Assert.Equal($"/performers/{PerformerId}/scenes", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.DoesNotContain("999", UnescapedQuery(handler), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("4520abc")]
    [InlineData("29&site_id=4520")]
    [InlineData("orderBy")]
    [InlineData("-1")]
    public async Task A_filter_id_that_is_not_digits_only_never_reaches_the_url(string malformed)
    {
        // The guard is ours because it cannot be the provider's: ThePornDB answers a malformed parameter with a
        // 200 and ignores it, leaving a bad value to fail QUIETLY upstream. The axis degrades to unapplied and the read
        // still returns its rows.
        var handler = FakeHttpMessageHandler.Json(Fixture());

        var result = await ProviderFrom(handler).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawSiteId], page: 1, perPage: 40,
            DiscoveryQuery.Default with { TagFilterId = malformed, PerformerFilterId = malformed }, default);

        Assert.True(result.IsOk);
        Assert.NotEmpty(result.Value!.Movies);
        var query = UnescapedQuery(handler);
        Assert.DoesNotContain(malformed, query, StringComparison.Ordinal);
        Assert.DoesNotContain("tags[", query, StringComparison.Ordinal);
        Assert.DoesNotContain("performers[", query, StringComparison.Ordinal);
        // The entity's own criterion is untouched by a dropped filter.
        Assert.Contains($"site_id={TushyRawSiteId}", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_reads_differing_only_in_the_year_produce_different_saturated_flags()
    {
        // The regression guard for a defect this project shipped and reverted: a filter can drop a set below the
        // 10,000 ceiling and make the count exact again (one tag alone reported 10,000; the same tag with
        // year=2019 reported 4,580). A flag carried over from the unfiltered read would render "10,000+" over an
        // exactly-known number.
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Respond(System.Net.HttpStatusCode.OK, "application/json", BodyWith(total: 10_000, rows: 3)),
            FakeHttpMessageHandler.Respond(System.Net.HttpStatusCode.OK, "application/json", BodyWith(total: 4_580, rows: 3)));
        var provider = ProviderFrom(handler);

        var saturated = await provider.EnumerateCataloguePageAsync(
            EntityKind.Tag, [NumericTagId], page: 1, perPage: 40, DiscoveryQuery.Default, default);
        var narrowed = await provider.EnumerateCataloguePageAsync(
            EntityKind.Tag, [NumericTagId], page: 1, perPage: 40,
            DiscoveryQuery.Default with { Year = 2019 }, default);

        Assert.True(saturated.Value!.TotalIsAtLeast);
        Assert.Equal(10_000, saturated.Value.Total);
        Assert.False(narrowed.Value!.TotalIsAtLeast);
        Assert.Equal(4_580, narrowed.Value.Total);
    }

    [Fact]
    public async Task A_short_page_ends_a_filtered_read_while_last_page_is_large_and_the_total_is_untouched()
    {
        // meta.last_page is a PAGE count and never a size: on one filtered set it read 6 / 2 / 1 at per_page
        // 10 / 40 / 100 while the total held at 56. A filtered read walked to its boundary returned 40 rows, then
        // 16 (SHORT — the last real page), then 0 past the end with no error.
        var lastRealPage = FakeHttpMessageHandler.Json(BodyWith(total: 56, rows: 16, lastPage: 2));
        var ended = await ProviderFrom(lastRealPage).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawSiteId], page: 2, perPage: 40,
            DiscoveryQuery.Default with { Year = 2024 }, default);

        Assert.False(ended.Value!.HasMore);
        Assert.Equal(56, ended.Value.Total);

        // A large last_page over a short page still ends the read — last_page never enters a size calculation.
        var inflated = FakeHttpMessageHandler.Json(BodyWith(total: 56, rows: 2, lastPage: 3334));
        var stopped = await ProviderFrom(inflated).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawSiteId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        Assert.False(stopped.Value!.HasMore);
        Assert.Equal(56, stopped.Value.Total);

        // A full page under the advertised last_page still reports another.
        var more = FakeHttpMessageHandler.Json(BodyWith(total: 56, rows: 40, lastPage: 2));
        var continued = await ProviderFrom(more).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawSiteId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        Assert.True(continued.Value!.HasMore);
    }

    [Fact]
    public async Task A_fully_filtered_paged_read_costs_exactly_one_provider_request()
    {
        // Every axis composes into ONE /scenes url: a fully filtered page costs what an unfiltered one costs.
        var filtered = FakeHttpMessageHandler.Json(Fixture());
        await ProviderFrom(filtered).EnumerateCataloguePageAsync(
            EntityKind.Tag, [NumericTagId], page: 1, perPage: 40,
            new DiscoveryQuery(DiscoverySortMode.Title, "4520", "289008", "29", 2019), default);

        Assert.Equal(1, filtered.CallCount);

        var plain = FakeHttpMessageHandler.Json(Fixture());
        await ProviderFrom(plain).EnumerateCataloguePageAsync(
            EntityKind.Tag, [NumericTagId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        Assert.Equal(1, plain.CallCount);
    }

    [Fact]
    public async Task A_page_carries_facet_options_built_from_theporndbs_own_numeric_ids()
    {
        var page = await ProviderFrom(FakeHttpMessageHandler.Json(Fixture())).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawSiteId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        var options = page.Value!.FacetOptions!;
        var site = Assert.Single(options.Studios!);
        Assert.Equal("247", site.Id);
        Assert.Equal("Tushy Raw", site.Label);
        // Ordered by label, which puts "Performer Three" before "Performer Two"; "Performer Four" carries no
        // parent in the fixture and is dropped, since an option a filter cannot be built from is not selectable.
        Assert.Equal(
            new[] { "Performer One", "Performer Three", "Performer Two" },
            options.Performers!.Select(o => o.Label));
        Assert.Equal(new[] { "289008", "289010", "289009" }, options.Performers!.Select(o => o.Id));
        Assert.Equal(new[] { "195", "10352" }, options.Tags!.Select(o => o.Id));
        Assert.Equal(new[] { "2024" }, options.Years!.Select(o => o.Id));
    }

    [Fact]
    public void Provider_declares_the_four_facet_axes_and_no_whole_set_aggregate()
    {
        var provider = ProviderFrom(FakeHttpMessageHandler.Json(Fixture()));

        Assert.Equal(
            new[]
            {
                DiscoveryFacetAxis.Studio, DiscoveryFacetAxis.Performer,
                DiscoveryFacetAxis.Tag, DiscoveryFacetAxis.Year,
            }.ToHashSet(),
            provider.ServerSideFacets.ToHashSet());

        // ThePornDB exposes no aggregate on any axis, so every v2 option list comes from the loaded page.
        foreach (var kind in Enum.GetValues<EntityKind>())
        {
            Assert.Empty(provider.WholeSetFacetAxes(kind));
        }
    }

    [Fact]
    public void Provider_declares_no_server_side_ordering()
    {
        var provider = ProviderFrom(FakeHttpMessageHandler.Json(Fixture()));

        Assert.True(
            provider.ServerSideSorts.Count == 0,
            "ThePornDB has no ordering to ask for: orderBy is a recognised /scenes parameter for which 32 "
            + "candidate values across two entities each answered 422, and sort / order return byte-identical "
            + "rows. A non-empty declaration here would have the tab claim the provider ordered a whole "
            + "catalogue it cannot order at all.");
    }

    [Fact]
    public async Task An_ordering_asked_of_this_provider_changes_nothing_it_sends()
    {
        // The declaration and the behaviour are the same fact stated twice: a source that declares no ordering
        // must also not smuggle one onto the wire. Every ordering produces one identical outbound request.
        var sent = new List<string>();
        foreach (var mode in Enum.GetValues<DiscoverySortMode>())
        {
            var handler = FakeHttpMessageHandler.Json(Fixture());
            await ProviderFrom(handler).EnumerateCataloguePageAsync(
                EntityKind.Studio, [TushyRawSiteId], page: 1, perPage: 40,
                DiscoveryQuery.Default with { Sort = mode }, default);

            Assert.Equal(1, handler.CallCount);
            sent.Add(handler.LastRequest!.RequestUri!.ToString());
        }

        Assert.Single(sent.Distinct(StringComparer.Ordinal));
    }
}
