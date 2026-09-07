using System.Text.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Discovery;
using WhisparrSync.Library;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Discovery;

/// <summary>
/// The offline drift-lock for the hand-rolled StashDB provider (the codegen substitute) — every test is
/// host-free and drives the transport with a <see cref="FakeHttpMessageHandler"/> primed from a captured,
/// content-safe <c>queryScenes</c> fixture (SFW allowlisted ids, no key, no explicit metadata). Proves the DTO
/// deserialization shape (a field rename/removal fails here), the scene→<c>WhisparrMovie</c> mapping the
/// diff keys on, the capability flags, and that only a read query is issued — no mutating operation.
/// </summary>
[Trait("Tier", "L0")]
public sealed class StashDbDiscoverySourceTests
{
    private const string TushyRawStudioId = "be4be46f-692f-4509-ba23-90a96abf0b16";
    private const string ChildStudioIdA = "11111111-1111-4111-8111-111111111111";
    private const string ChildStudioIdB = "22222222-2222-4222-8222-222222222222";
    private const string ForeignStudioId = "99999999-9999-4999-8999-999999999999";
    private const string ForeignPerformerId = "88888888-8888-4888-8888-888888888888";
    private const string ForeignTagId = "77777777-7777-4777-8777-777777777777";

    private static string Fixture()
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestSupport", "fixtures", "stashdb-queryScenes-studio.json"));

    private static StashDbDiscoverySource ProviderFrom(FakeHttpMessageHandler handler)
        => new(new StashDbGraphQlClient(new HttpClient(handler)), "https://stashdb.org/graphql", "test-key");

    [Fact]
    public void Fixture_deserializes_to_the_pinned_queryScenes_shape()
    {
        // The shape lock: the captured response binds into the hand-rolled DTO with the expected count
        // and scene ids. A renamed/removed field (count, scenes, id, release_date, studio.name) breaks this.
        var response = JsonSerializer.Deserialize(
            Fixture(), StashDbJsonContext.Default.StashDbGraphQlResponse);

        var scenes = response!.Data!.QueryScenes!;
        Assert.Equal(390, scenes.Count);
        Assert.Equal(3, scenes.Scenes!.Length);
        Assert.Equal("019f3391-e257-7dd7-8c79-e399f2ad6ca5", scenes.Scenes[0].Id);
        Assert.Equal("2024-01-05", scenes.Scenes[0].ReleaseDate);
        Assert.Equal("Tushy Raw", scenes.Scenes[0].Studio!.Name);
        // The enriched facets bind through the join shape (performers[].performer.{name,images}, tags[].name,
        // scene.details) — a rename/removal of any of those nested fields fails here.
        Assert.Equal(
            new[] { "Performer One", "Performer Two" },
            scenes.Scenes[0].Performers!.Select(p => p.Performer!.Name));
        Assert.Equal(
            "https://cdn.example.test/performer/1.jpg",
            scenes.Scenes[0].Performers![0].Performer!.Images![0].Url);
        Assert.Equal(new[] { "Tag Alpha", "Tag Beta" }, scenes.Scenes[0].Tags!.Select(t => t.Name));
        Assert.Equal("A synthetic scene blurb used only to pin the overview mapping.", scenes.Scenes[0].Details);
        // The provider's own filter ids on the join nodes — what a facet option read off a rendered page is sent
        // back as. A rename or removal of either selection field fails here.
        Assert.Equal("019f3392-0001-7dd7-8c79-e399f2ad6c01", scenes.Scenes[0].Performers![0].Performer!.Id);
        Assert.Equal("019f3393-0001-7dd7-8c79-e399f2ad6d01", scenes.Scenes[0].Tags![0].Id);
    }

    [Fact]
    public async Task EnumerateCatalogueAsync_maps_scenes_to_diff_ready_movies()
    {
        var provider = ProviderFrom(FakeHttpMessageHandler.Json(Fixture()));

        var result = await provider.EnumerateCatalogueAsync(EntityKind.Studio, [TushyRawStudioId], default);

        Assert.True(result.IsOk);
        var movies = result.Value!.Movies;
        Assert.Equal(3, movies.Length);

        var first = movies[0];
        // StashId is the StashDB scene id (the StashDb id family the diff keys on); ItemType "scene"; the
        // release date + studio name carry through; the scene's first screenshot maps to a landscape cover the
        // card renders (a source-served url, so it rides RemoteUrl), while the poster kind is still absent.
        Assert.Equal("019f3391-e257-7dd7-8c79-e399f2ad6ca5", first.StashId);
        Assert.Equal("scene", first.ItemType);
        Assert.Equal("2024-01-05", first.ReleaseDate);
        Assert.Equal("Tushy Raw", first.StudioTitle);
        var cover = Assert.Single(first.Images!);
        Assert.Equal("screenshot", cover.CoverType);
        Assert.False(string.IsNullOrEmpty(cover.RemoteUrl));
        // The performer names + index-aligned avatar urls (empty string when the performer has no image), the
        // tag names, and the scene overview all carry onto the movie the card reads.
        Assert.Equal(new[] { "Performer One", "Performer Two" }, first.PerformerNames);
        Assert.Equal(new[] { "https://cdn.example.test/performer/1.jpg", "" }, first.PerformerImageUrls);
        Assert.Equal(new[] { "Tag Alpha", "Tag Beta" }, first.TagNames);
        Assert.Equal("A synthetic scene blurb used only to pin the overview mapping.", first.Overview);

        // Empty/absent facets bind null, never an empty array (the absent-vs-present-but-empty distinction).
        Assert.Equal(new[] { "Performer Three" }, movies[1].PerformerNames);
        Assert.Null(movies[1].TagNames);
        Assert.Null(movies[2].PerformerNames);
        Assert.Null(movies[2].TagNames);
    }

    [Fact]
    public async Task Mapped_movies_flow_through_DiscoveryService_diff_unchanged()
    {
        // The provider-agnostic promise: the mapped rows subtract by StashDB id in the pure diff with
        // no reshape. Owning scene 1 leaves scenes 2 and 3 missing.
        var provider = ProviderFrom(FakeHttpMessageHandler.Json(Fixture()));
        var catalogue = (await provider.EnumerateCatalogueAsync(EntityKind.Studio, [TushyRawStudioId], default)).Value!;
        var owned = new[]
        {
            new CoveVideo(10, "Owned", new DateOnly(2024, 1, 5),
                ["019f3391-e257-7dd7-8c79-e399f2ad6ca5"], [], [], []),
        };

        var missing = DiscoveryService.Diff(catalogue.Movies, owned, [], "Tushy Raw");

        Assert.Equal(
            new[] { "019f3391-e257-7dd7-8c79-e399f2ad6ca6", "019f3391-e257-7dd7-8c79-e399f2ad6ca7" },
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
    public async Task EnumerateCatalogueAsync_issues_exactly_one_read_query_and_no_mutation()
    {
        // Read-only lock: one page fits under per_page, so the studio read is a single POST carrying the
        // queryScenes QUERY — never a GraphQL mutation, and the ApiKey (not X-Api-Key) header is what auths it.
        var handler = FakeHttpMessageHandler.Json(Fixture());
        var provider = ProviderFrom(handler);

        await provider.EnumerateCatalogueAsync(EntityKind.Studio, [TushyRawStudioId], default);

        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("queryScenes", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation", handler.LastRequestBody!, StringComparison.OrdinalIgnoreCase);
        Assert.True(handler.LastRequest.Headers.Contains("ApiKey"));
    }

    [Fact]
    public async Task Performer_kind_enumerates_scenes_via_queryScenes_performers()
    {
        // 61-02: the performer catalogue reads queryScenes(performers INCLUDES) — the same response shape and the
        // same scene→WhisparrMovie mapping as the studio path, differing ONLY in the filter criterion. The
        // captured fixture's response does not echo the filter, so it stands in for the performer read; the sent
        // body is asserted to carry the performers criterion (not studios).
        var handler = FakeHttpMessageHandler.Json(Fixture());
        var provider = ProviderFrom(handler);

        var result = await provider.EnumerateCatalogueAsync(
            EntityKind.Performer, ["performer-uuid-0001"], default);

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value!.Movies.Length);
        Assert.Equal("019f3391-e257-7dd7-8c79-e399f2ad6ca5", result.Value!.Movies[0].StashId);
        Assert.Equal("scene", result.Value!.Movies[0].ItemType);
        Assert.Equal(1, handler.CallCount);
        // The performer read carries the performers filter (never the studios one) and stays a read query.
        Assert.Contains("performers", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"studios\"", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation", handler.LastRequestBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Tag_kind_enumerates_scenes_via_queryScenes_tags()
    {
        // The tag catalogue reads queryScenes(tags INCLUDES) — the same response shape and the same
        // scene→WhisparrMovie mapping as the studio/performer paths, differing ONLY in the filter criterion. The
        // captured fixture stands in for the tag read; the sent body carries the tags criterion (never studios /
        // performers) and stays a read query. (The quoted "tags"/"studios"/"performers" target the input
        // criterion, not the selection set whose tags/performers fields are unquoted.)
        var handler = FakeHttpMessageHandler.Json(Fixture());
        var provider = ProviderFrom(handler);

        var result = await provider.EnumerateCatalogueAsync(EntityKind.Tag, ["tag-uuid-0001"], default);

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value!.Movies.Length);
        Assert.Equal("019f3391-e257-7dd7-8c79-e399f2ad6ca5", result.Value!.Movies[0].StashId);
        Assert.Equal("scene", result.Value!.Movies[0].ItemType);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("\"tags\"", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"studios\"", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"performers\"", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation", handler.LastRequestBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Whole_catalogue_read_costs_one_call_per_page_while_one_page_costs_exactly_one()
    {
        // The measurement behind bounding an action's re-derive to one page: the full-loop read walks the
        // advertised count at 100 rows a call, so its cost tracks catalogue SIZE (three calls for 250 rows, and
        // the loop's own 60-page ceiling for a six-figure catalogue), while a paged read is exactly one call
        // whatever the catalogue holds. The two legs share one fixture, so only the read shape differs.
        var fullPage = SyntheticPage(count: 250, rows: 100);

        var loopHandler = FakeHttpMessageHandler.Json(fullPage);
        var whole = await ProviderFrom(loopHandler).EnumerateCatalogueAsync(
            EntityKind.Studio, [TushyRawStudioId], default);

        Assert.True(whole.IsOk);
        Assert.Equal(3, loopHandler.CallCount);
        Assert.Equal(300, whole.Value!.Movies.Length);

        // A larger catalogue on the identical shape: the loop's call count follows the advertised count. Two
        // differing points, because one number alone cannot distinguish a constant from a function of size.
        var biggerHandler = FakeHttpMessageHandler.Json(SyntheticPage(count: 550, rows: 100));
        var bigger = await ProviderFrom(biggerHandler).EnumerateCatalogueAsync(
            EntityKind.Studio, [TushyRawStudioId], default);

        Assert.True(bigger.IsOk);
        Assert.Equal(6, biggerHandler.CallCount);

        var pageHandler = FakeHttpMessageHandler.Json(fullPage);
        var onePage = await ProviderFrom(pageHandler).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawStudioId], page: 1, perPage: 100, DiscoveryQuery.Default, default);

        Assert.True(onePage.IsOk);
        Assert.Equal(1, pageHandler.CallCount);
        Assert.Equal(100, onePage.Value!.Movies.Length);
        Assert.Equal(250, onePage.Value.Total);
        Assert.True(onePage.Value.HasMore);
    }

    // A synthetic queryScenes page: the advertised count plus `rows` scenes with distinct ids. Synthetic ids and
    // titles only — the captured fixture stays the shape lock, this one exists to vary the row COUNT.
    private static string SyntheticPage(int count, int rows)
        => JsonSerializer.Serialize(new
        {
            data = new
            {
                queryScenes = new
                {
                    count,
                    scenes = Enumerable.Range(0, rows).Select(i => new
                    {
                        id = $"00000000-0000-4000-8000-{i:D12}",
                        title = $"Synthetic Scene {i}",
                        release_date = "2024-01-05",
                    }).ToArray(),
                },
            },
        });

    // A synthetic queryScenes page with explicit release dates, performer ids and tag ids. The captured fixture
    // stays the shape lock; this one exists to vary the facet VALUES a page carries.
    private sealed record Row(
        string Id, string? Date, (string Id, string Name)[] Performers, (string Id, string Name)[] Tags);

    private static string PageOf(params Row[] rows)
        => JsonSerializer.Serialize(new
        {
            data = new
            {
                queryScenes = new
                {
                    count = rows.Length,
                    scenes = rows.Select(r => new
                    {
                        id = r.Id,
                        title = $"Scene {r.Id}",
                        release_date = r.Date,
                        studio = new { id = "studio-1", name = "Studio One" },
                        performers = r.Performers
                            .Select(p => new { performer = new { id = p.Id, name = p.Name } }).ToArray(),
                        tags = r.Tags.Select(t => new { id = t.Id, name = t.Name }).ToArray(),
                    }).ToArray(),
                },
            },
        });

    [Fact]
    public async Task A_page_carries_facet_options_built_from_the_providers_own_ids()
    {
        // An option is an {id,label} pair because both providers filter by id and never by display name: this is
        // what lets a value read off a rendered page still narrow the WHOLE catalogue once it is selected. The
        // ids ride the option list, never the row DTOs the diff and the card read.
        var provider = ProviderFrom(FakeHttpMessageHandler.Json(Fixture()));

        var page = await provider.EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawStudioId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        var options = page.Value!.FacetOptions!;
        // Ordered by label, case-insensitively, which puts "Performer Three" before "Performer Two".
        Assert.Equal(
            new[] { "Performer One", "Performer Three", "Performer Two" },
            options.Performers!.Select(o => o.Label));
        Assert.Equal(
            new[]
            {
                "019f3392-0001-7dd7-8c79-e399f2ad6c01",
                "019f3392-0003-7dd7-8c79-e399f2ad6c03",
                "019f3392-0002-7dd7-8c79-e399f2ad6c02",
            },
            options.Performers!.Select(o => o.Id));
        Assert.Equal(new[] { "Tag Alpha", "Tag Beta" }, options.Tags!.Select(o => o.Label));
        Assert.Equal(
            new[] { "019f3393-0001-7dd7-8c79-e399f2ad6d01", "019f3393-0002-7dd7-8c79-e399f2ad6d02" },
            options.Tags!.Select(o => o.Id));
        var studio = Assert.Single(options.Studios!);
        Assert.Equal(TushyRawStudioId, studio.Id);
        Assert.Equal("Tushy Raw", studio.Label);
        var year = Assert.Single(options.Years!);
        Assert.Equal("2024", year.Id);
        Assert.Equal("2024", year.Label);
    }

    [Fact]
    public async Task Two_performers_sharing_a_display_name_yield_one_option()
    {
        // The list is keyed on the LABEL, and two distinct provider ids under one display name collapse to a
        // single option carrying the first id encountered. That collapse is already how the client derives the
        // same list from row names, and its cost is that only one of the two can be narrowed on by id.
        var handler = FakeHttpMessageHandler.Json(PageOf(
            new Row("s-1", "2024-01-05", [("p-aa", "Alex Doe")], []),
            new Row("s-2", "2024-02-05", [("p-bb", "Alex Doe")], [])));

        var page = await ProviderFrom(handler).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawStudioId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        var option = Assert.Single(page.Value!.FacetOptions!.Performers!);
        Assert.Equal("Alex Doe", option.Label);
        Assert.Equal("p-aa", option.Id);
    }

    [Fact]
    public async Task A_year_query_keeps_bare_year_and_year_month_rows_and_drops_the_adjacent_one()
    {
        // The single date bound over-returns by construction, and it is loose at the boundary because a StashDB
        // release date is not always a full ISO date: date < 2017-01-01 returns rows dated "2016" and "2016-01"
        // alongside 2015's. The trim reads the leading four digits, the same rule the client reads a bare year by.
        var handler = FakeHttpMessageHandler.Json(PageOf(
            new Row("s-bare", "2016", [], []),
            new Row("s-month", "2016-01", [], []),
            new Row("s-prev", "2015-12-31", [], []),
            new Row("s-next", "2017-01-01", [], [])));

        var page = await ProviderFrom(handler).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawStudioId], page: 1, perPage: 40,
            DiscoveryQuery.Default with { Year = 2016 }, default);

        Assert.Equal(new[] { "s-bare", "s-month" }, page.Value!.Movies.Select(m => m.StashId));
        // The trim moves ROWS only: the reported total stays the provider's own filtered catalogue count, which
        // this emulation leaves a slight over-estimate.
        Assert.Equal(4, page.Value.Total);
        // The year options describe the page that was actually returned, never the rows the bound over-fetched.
        var year = Assert.Single(page.Value.FacetOptions!.Years!);
        Assert.Equal("2016", year.Id);
    }

    [Fact]
    public async Task Every_kind_and_every_filter_combination_still_carries_the_entitys_own_criterion()
    {
        // The mitigation the client-supplied-id posture rests on. A caller now supplies PROVIDER ids, and
        // what keeps that safe is structural, not a check: the entity's server-resolved criterion is
        // unconditional and a query filter is only ever an ADDITIONAL ANDed criterion. Driven as a matrix over
        // every served kind and a bit-set over the four filter axes, which means a fifth axis has to be added
        // here to be covered at all.
        var kinds = new[] { EntityKind.Studio, EntityKind.Performer, EntityKind.Tag };
        const string EntityRemoteId = "019f3390-0000-7000-8000-00000000000e";
        var cases = 0;

        foreach (var kind in kinds)
        {
            foreach (var bits in Enumerable.Range(0, 1 << 4))
            {
                var handler = FakeHttpMessageHandler.Json(Fixture());
                var query = DiscoveryQuery.Default with
                {
                    StudioFilterId = (bits & 1) != 0 ? ForeignStudioId : null,
                    PerformerFilterId = (bits & 2) != 0 ? ForeignPerformerId : null,
                    TagFilterId = (bits & 4) != 0 ? ForeignTagId : null,
                    Year = (bits & 8) != 0 ? 2016 : null,
                };

                await ProviderFrom(handler).EnumerateCataloguePageAsync(
                    kind, [EntityRemoteId], page: 1, perPage: 40, query, default);

                var criterion = kind switch
                {
                    EntityKind.Studio => "studios",
                    EntityKind.Performer => "performers",
                    _ => "tags",
                };
                var expected = $"\"{criterion}\":{{\"value\":[\"{EntityRemoteId}\"],\"modifier\":\"INCLUDES\"}}";
                Assert.True(
                    handler.LastRequestBody!.Contains(expected, StringComparison.Ordinal),
                    $"{kind} with filter axes {bits:X} lost its own criterion. A client-supplied filter id may "
                    + $"only NARROW this entity's own catalogue and can never widen the read to a foreign one. "
                    + $"Sent: {handler.LastRequestBody}");
                cases++;
            }
        }

        // Pinned: a matrix that quietly shrinks then fails, and cannot pass on fewer cases.
        Assert.Equal(48, cases);
    }

    [Fact]
    public async Task A_sub_studio_filter_narrows_within_the_parent_union_and_a_non_member_is_dropped()
    {
        // The one place a query id REPLACES the entity's criterion. A parent studio's read unions parent + child
        // ids; narrowing to one child is exactly what the user asked for and stays a subset of that union. The
        // id is honoured only when it is a MEMBER — a crafted non-member is dropped and the union stands, which
        // is the case where it would otherwise reach the provider.
        IReadOnlyList<string> union = [TushyRawStudioId, ChildStudioIdA, ChildStudioIdB];

        var member = FakeHttpMessageHandler.Json(Fixture());
        await ProviderFrom(member).EnumerateCataloguePageAsync(
            EntityKind.Studio, union, page: 1, perPage: 40,
            DiscoveryQuery.Default with { StudioFilterId = ChildStudioIdB }, default);

        Assert.Contains(
            $"\"studios\":{{\"value\":[\"{ChildStudioIdB}\"],\"modifier\":\"INCLUDES\"}}",
            member.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain(ChildStudioIdA, member.LastRequestBody!, StringComparison.Ordinal);

        var foreign = FakeHttpMessageHandler.Json(Fixture());
        await ProviderFrom(foreign).EnumerateCataloguePageAsync(
            EntityKind.Studio, union, page: 1, perPage: 40,
            DiscoveryQuery.Default with { StudioFilterId = ForeignStudioId }, default);

        Assert.Contains(
            $"\"studios\":{{\"value\":[\"{TushyRawStudioId}\",\"{ChildStudioIdA}\",\"{ChildStudioIdB}\"],"
            + "\"modifier\":\"INCLUDES\"}",
            foreign.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain(ForeignStudioId, foreign.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_fully_filtered_paged_read_costs_exactly_one_provider_request()
    {
        // The bounding proof for this provider: a sort plus all four filters travel in the SAME
        // queryScenes request, and an unfiltered read costs the same one.
        var filtered = FakeHttpMessageHandler.Json(Fixture());
        await ProviderFrom(filtered).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawStudioId], page: 1, perPage: 40,
            new DiscoveryQuery(
                DiscoverySortMode.Title, ForeignStudioId, ForeignPerformerId, ForeignTagId, 2016),
            default);

        Assert.Equal(1, filtered.CallCount);

        var plain = FakeHttpMessageHandler.Json(Fixture());
        await ProviderFrom(plain).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawStudioId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        Assert.Equal(1, plain.CallCount);
    }

    [Fact]
    public void Provider_declares_the_four_facet_axes_and_one_aggregate_per_kind_that_has_one()
    {
        // The declaration is what the tab words its controls from: an axis claimed here but not applied would
        // tell the user the whole catalogue narrowed when only the loaded rows did.
        var provider = ProviderFrom(FakeHttpMessageHandler.Json(Fixture()));

        Assert.Equal(
            new[]
            {
                DiscoveryFacetAxis.Studio, DiscoveryFacetAxis.Performer,
                DiscoveryFacetAxis.Tag, DiscoveryFacetAxis.Year,
            }.ToHashSet(),
            provider.ServerSideFacets.ToHashSet());

        // Exactly two cells have a provider aggregate: a studio page's PERFORMER axis and a performer page's
        // STUDIO axis. Naming them one by one keeps a widened claim visible.
        Assert.Equal(
            new[] { DiscoveryFacetAxis.Performer }.ToHashSet(),
            provider.WholeSetFacetAxes(EntityKind.Studio).ToHashSet());
        Assert.Equal(
            new[] { DiscoveryFacetAxis.Studio }.ToHashSet(),
            provider.WholeSetFacetAxes(EntityKind.Performer).ToHashSet());

        // A TAG page has no aggregate on ANY axis, permanently: the performer query input carries no tag filter
        // and neither does the studio query. Its option lists can only be the values its rendered rows carry.
        Assert.Empty(provider.WholeSetFacetAxes(EntityKind.Tag));

        // The YEAR axis is never claimed, on any kind — a date probe would buy a range and not an option list.
        foreach (var kind in Enum.GetValues<EntityKind>())
        {
            Assert.DoesNotContain(DiscoveryFacetAxis.Year, provider.WholeSetFacetAxes(kind));
        }
    }

    [Fact]
    public void Provider_declares_all_three_orderings_as_server_side()
    {
        // Every ordering the sort control offers is native on queryScenes, so none of the three degrades to a
        // loaded-rows sort here — measured live across four entities, each returning the same count under all
        // three. A shrunken declaration would make the tab word an ordering it does apply as one it does not.
        var provider = ProviderFrom(FakeHttpMessageHandler.Json(Fixture()));

        Assert.Equal(
            new[] { DiscoverySortMode.Newest, DiscoverySortMode.Oldest, DiscoverySortMode.Title }.ToHashSet(),
            provider.ServerSideSorts.ToHashSet());
    }

    [Theory]
    [InlineData(DiscoverySortModeName.Newest, "\"sort\":\"DATE\"", "\"direction\":\"DESC\"")]
    [InlineData(DiscoverySortModeName.Oldest, "\"sort\":\"DATE\"", "\"direction\":\"ASC\"")]
    [InlineData(DiscoverySortModeName.Title, "\"sort\":\"TITLE\"", "\"direction\":\"ASC\"")]
    public async Task A_paged_read_puts_the_asked_ordering_on_the_serialized_scene_query_input(
        string modeName, string expectedSort, string expectedDirection)
    {
        // The ordering must reach the PROVIDER's input, not a client-side re-sort of the page that came back.
        // Asserted against the serialized body, which is the only place the provider actually reads it from.
        var handler = FakeHttpMessageHandler.Json(Fixture());
        var provider = ProviderFrom(handler);
        var query = DiscoveryQuery.Default with { Sort = ModeFor(modeName) };

        await provider.EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawStudioId], page: 1, perPage: 40, query, default);

        Assert.Contains(expectedSort, handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains(expectedDirection, handler.LastRequestBody!, StringComparison.Ordinal);
        // One outbound request whatever the ordering: asking for a different order must not become a fill loop.
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task The_two_set_changing_provider_sorts_are_unreachable_from_any_ordering()
    {
        // A ranked feed returns a different-sized SET for the same input (390 under a date ordering, 216 under
        // trending, measured live), which would silently change what "missing" means. No ordering can send one.
        var handler = FakeHttpMessageHandler.Json(Fixture());
        var provider = ProviderFrom(handler);

        foreach (var mode in Enum.GetValues<DiscoverySortMode>())
        {
            await provider.EnumerateCataloguePageAsync(
                EntityKind.Studio, [TushyRawStudioId], page: 1, perPage: 40,
                DiscoveryQuery.Default with { Sort = mode }, default);

            Assert.DoesNotContain("TRENDING", handler.LastRequestBody!, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("POPULARITY", handler.LastRequestBody!, StringComparison.OrdinalIgnoreCase);
        }
    }

    // The wire literals, repeated here because an InlineData argument must be a compile-time constant and the
    // enum is internal (an internal type cannot appear in a public test method's signature).
    private static class DiscoverySortModeName
    {
        public const string Newest = "newest";
        public const string Oldest = "oldest";
        public const string Title = "title";
    }

    private static DiscoverySortMode ModeFor(string name)
        => DiscoveryQueryGuard.Normalize(new DiscoveryQueryRequest(Sort: name), isV2: false).Sort;

    [Fact]
    public async Task Tag_kind_pages_via_queryScenes_tags_mapping_scenes_like_the_studio_path()
    {
        // ONE upstream page per call routes to the tags criterion just like the full-loop read, and maps each
        // scene into the same WhisparrMovie shape (StashId = the scene id, ItemType "scene").
        var handler = FakeHttpMessageHandler.Json(Fixture());
        var provider = ProviderFrom(handler);

        var result = await provider.EnumerateCataloguePageAsync(
            EntityKind.Tag, ["tag-uuid-0001"], page: 1, perPage: 100, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value!.Movies.Length);
        Assert.Equal("019f3391-e257-7dd7-8c79-e399f2ad6ca5", result.Value.Movies[0].StashId);
        Assert.Equal("scene", result.Value.Movies[0].ItemType);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("\"tags\"", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"studios\"", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"performers\"", handler.LastRequestBody!, StringComparison.Ordinal);
    }

    // ---- the whole-set aggregates ----

    private const string RosterPerformerA = "019f4001-0000-7dd7-8c79-e399f2ad0001";
    private const string RosterPerformerB = "019f4001-0000-7dd7-8c79-e399f2ad0002";
    private const string AggregateStudioA = "019f4002-0000-7dd7-8c79-e399f2ad0001";

    // A queryPerformers roster body: the named rows, and the count the provider says the roster holds.
    private static string RosterBody(int count, params string[] names)
    {
        var rows = string.Join(",", names.Select((name, i) =>
            $"{{\"id\":\"019f4001-0000-7dd7-8c79-e399f2ad000{i + 1}\",\"name\":\"{name}\"}}"));
        return $"{{\"data\":{{\"queryPerformers\":{{\"count\":{count},\"performers\":[{rows}]}}}}}}";
    }

    private static string StudioListBody(params string[] names)
    {
        var rows = string.Join(",", names.Select((name, i) =>
            $"{{\"studio\":{{\"id\":\"019f4002-0000-7dd7-8c79-e399f2ad000{i + 1}\",\"name\":\"{name}\"}}}}"));
        return $"{{\"data\":{{\"findPerformer\":{{\"studios\":[{rows}]}}}}}}";
    }

    private static Func<HttpResponseMessage> Body(string json)
        => FakeHttpMessageHandler.Respond(System.Net.HttpStatusCode.OK, "application/json", json);

    // A memo over the SHIPPED slot type and the SHIPPED key composition, minus the page and the query — the two
    // lines the endpoint's own wrapper is. A dictionary here would prove only that this test can count.
    private static DiscoveryAggregateMemo Memo()
    {
        var cache = new TtlCache<DiscoveryFacetOptions>(TimeSpan.FromMinutes(5));
        return (kind, remoteIds, fetch, ct) => cache.GetAsync(
            DiscoveryCacheKeys.For(
                "v3", "https://stashdb.org/graphql", "test-key", kind, remoteIds, page: null,
                DiscoveryQuery.Default),
            fetch, ct);
    }

    private static StashDbDiscoverySource ProviderWithMemo(FakeHttpMessageHandler handler)
        => new(new StashDbGraphQlClient(new HttpClient(handler)), "https://stashdb.org/graphql", "test-key",
            null, Memo());

    private static int AggregateCalls(FakeHttpMessageHandler handler, string operation)
        => handler.Requests.Count(r => r.Body?.Contains(operation, StringComparison.Ordinal) == true);

    [Fact]
    public async Task A_studio_page_serves_the_whole_roster_on_the_performer_axis_and_claims_only_that_axis()
    {
        // The roster REPLACES the page's own performer values — the point of the aggregate is that a dropdown on
        // a 390-scene studio offers the studio's whole cast and not the three names this page happened to carry.
        var handler = FakeHttpMessageHandler.Sequence(
            Body(Fixture()), Body(RosterBody(count: 2, "Roster One", "Roster Two")));

        var result = await ProviderWithMemo(handler).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawStudioId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        var options = result.Value!.FacetOptions!;
        Assert.Equal(new[] { "Roster One", "Roster Two" }, options.Performers!.Select(o => o.Label));
        Assert.Equal(new[] { RosterPerformerA, RosterPerformerB }, options.Performers!.Select(o => o.Id));

        // Every other axis keeps the page's own values, and only the performer axis is CLAIMED whole-set. The
        // claim is what suppresses the page-derived label; a widened one IS the over-claim.
        Assert.Equal(new[] { "Tag Alpha", "Tag Beta" }, options.Tags!.Select(o => o.Label));
        Assert.Equal(new[] { "Tushy Raw" }, options.Studios!.Select(o => o.Label));
        Assert.Equal(
            new[] { DiscoveryFacetAxis.Performer }.ToHashSet(), result.Value.WholeSetAxes!.ToHashSet());
    }

    [Fact]
    public async Task A_performer_page_serves_the_whole_studio_list_on_the_studio_axis()
    {
        var handler = FakeHttpMessageHandler.Sequence(
            Body(Fixture()), Body(StudioListBody("Studio Alpha", "Studio Beta")));

        var result = await ProviderWithMemo(handler).EnumerateCataloguePageAsync(
            EntityKind.Performer, ["019f3392-0001-7dd7-8c79-e399f2ad6c01"], page: 1, perPage: 40,
            DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        var options = result.Value!.FacetOptions!;
        Assert.Equal(new[] { "Studio Alpha", "Studio Beta" }, options.Studios!.Select(o => o.Label));
        Assert.Equal(AggregateStudioA, options.Studios![0].Id);
        Assert.Equal(new[] { DiscoveryFacetAxis.Studio }.ToHashSet(), result.Value.WholeSetAxes!.ToHashSet());
        // The page's own performer values survive: this axis has no aggregate on a performer page.
        Assert.Equal(
            new[] { "Performer One", "Performer Three", "Performer Two" },
            options.Performers!.Select(o => o.Label));
    }

    [Fact]
    public async Task Three_page_reads_of_one_entity_issue_the_aggregate_exactly_once()
    {
        // The aggregate describes the ENTITY, so paging must not re-buy it. Three reads, one roster.
        var handler = FakeHttpMessageHandler.Sequence(
            Body(Fixture()), Body(RosterBody(count: 2, "Roster One", "Roster Two")), Body(Fixture()));
        var provider = ProviderWithMemo(handler);

        for (var page = 1; page <= 3; page++)
        {
            var result = await provider.EnumerateCataloguePageAsync(
                EntityKind.Studio, [TushyRawStudioId], page, perPage: 40, DiscoveryQuery.Default, default);

            // Every page still serves the roster — the memo is a cost bound, not a first-page-only feature.
            Assert.Equal(
                new[] { "Roster One", "Roster Two" },
                result.Value!.FacetOptions!.Performers!.Select(o => o.Label));
        }

        Assert.Equal(1, AggregateCalls(handler, "queryPerformers"));
        Assert.Equal(3, AggregateCalls(handler, "queryScenes"));
        Assert.Equal(4, handler.CallCount);
    }

    [Fact]
    public async Task A_tag_page_read_issues_exactly_one_provider_request()
    {
        // A kind with no aggregate pays nothing for one, memo or not.
        var handler = FakeHttpMessageHandler.Sequence(Body(Fixture()));

        var result = await ProviderWithMemo(handler).EnumerateCataloguePageAsync(
            EntityKind.Tag, ["tag-uuid-0001"], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(1, handler.CallCount);
        Assert.Null(result.Value!.WholeSetAxes);
        Assert.Equal(0, AggregateCalls(handler, "queryPerformers"));
    }

    [Fact]
    public async Task A_failing_aggregate_leaves_an_ok_page_with_the_page_s_own_options_and_no_claim()
    {
        // A metadata-source hiccup on the aggregate must not cost the catalogue page, and must not empty a
        // working dropdown either: the page's own values stand, unclaimed.
        var handler = FakeHttpMessageHandler.Sequence(
            Body(Fixture()),
            FakeHttpMessageHandler.Respond(System.Net.HttpStatusCode.BadGateway, "text/html", "<html>502</html>"));

        var result = await ProviderWithMemo(handler).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawStudioId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value!.Movies.Length);
        Assert.Equal(
            new[] { "Performer One", "Performer Three", "Performer Two" },
            result.Value.FacetOptions!.Performers!.Select(o => o.Label));
        Assert.Null(result.Value.WholeSetAxes);
    }

    [Fact]
    public async Task A_roster_larger_than_its_page_is_dropped_and_never_offered_as_a_prefix()
    {
        // The truncation rule. An alphabetical prefix of a roster covers neither the set nor the rendered rows,
        // and no honest label exists for it — measured: one studio's 1696 performers do not fit the bound.
        var handler = FakeHttpMessageHandler.Sequence(
            Body(Fixture()), Body(RosterBody(count: 1696, "Roster One", "Roster Two")));

        var result = await ProviderWithMemo(handler).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawStudioId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(
            new[] { "Performer One", "Performer Three", "Performer Two" },
            result.Value!.FacetOptions!.Performers!.Select(o => o.Label));
        Assert.Null(result.Value.WholeSetAxes);
    }

    [Fact]
    public async Task A_parent_studios_union_asks_for_no_roster_at_all()
    {
        // The aggregate takes ONE studio id while a parent's catalogue read unions its network. Asking for one
        // child's roster would answer a question the page did not put.
        var handler = FakeHttpMessageHandler.Sequence(Body(Fixture()));

        var result = await ProviderWithMemo(handler).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawStudioId, ChildStudioIdA, ChildStudioIdB], page: 1, perPage: 40,
            DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(1, handler.CallCount);
        Assert.Null(result.Value!.WholeSetAxes);
    }

    [Fact]
    public async Task A_source_built_without_a_memo_reads_no_aggregate()
    {
        // The fail-safe direction: no slot to remember it in means the aggregate is not read at all, since an
        // uncached one would be paid on every page. The page-derived options stand, unclaimed.
        var handler = FakeHttpMessageHandler.Sequence(Body(Fixture()));

        var result = await ProviderFrom(handler).EnumerateCataloguePageAsync(
            EntityKind.Studio, [TushyRawStudioId], page: 1, perPage: 40, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(1, handler.CallCount);
        Assert.Null(result.Value!.WholeSetAxes);
    }
}
