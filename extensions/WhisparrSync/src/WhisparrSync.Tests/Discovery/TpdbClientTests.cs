using System.Net;
using WhisparrSync.Client;
using WhisparrSync.Discovery;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Discovery;

/// <summary>
/// The offline lock for the single-page ThePornDB REST read (the v2 Missing tab's page fetch): host-free, the
/// transport driven by a <see cref="FakeHttpMessageHandler"/>. It proves the read issues EXACTLY ONE upstream
/// GET per page (never the full pagination loop), infers end-of-catalogue from a SHORT page (ThePornDB's count
/// is a placeholder for a gated query), and classifies a non-Ok upstream instead of throwing — so a source
/// outage or a missing token can never surface as an empty own-everything page.
/// </summary>
[Trait("Tier", "L0")]
public sealed class TpdbClientTests
{
    private const string RestBaseUrl = "https://api.theporndb.net";
    private const string SiteId = "247";

    // A canonical ThePornDB performer id is a UUID — the id Cove stores as the performer's ThePornDB remote id.
    private const string PerformerId = "3de6cd92-0000-4000-8000-000000000001";

    // A ThePornDB tag id is NUMERIC (its uuid is a secondary field); the numeric one is what the filter keys on.
    private const string NumericTagId = "202";

    private static TpdbClient ClientFrom(FakeHttpMessageHandler handler)
        => new(new HttpClient(handler));

    // A /scenes 200 body carrying sceneCount rows plus a REST pagination block.
    private static string Page(int sceneCount, int currentPage, int lastPage)
    {
        var scenes = string.Join(",", Enumerable.Range(0, sceneCount).Select(i =>
            $"{{\"id\":\"scene-{i}\",\"title\":\"Scene {i}\",\"date\":null,\"poster\":null,\"site\":null}}"));
        return $"{{\"data\":[{scenes}],\"meta\":{{\"current_page\":{currentPage},\"last_page\":{lastPage}}}}}";
    }

    /// <summary>
    /// The whole-catalogue read stops at its page ceiling, and SAYS SO. Without the discriminator a capped read
    /// is shape-identical to an exhausted one, and the Missing diff is catalogue − owned − excluded — so the tab
    /// silently under-reports missing scenes for exactly the largest sites. Raising the ceiling only moves the
    /// point at which that happens, which is why the signal is the fix.
    /// </summary>
    [Fact]
    public async Task A_catalogue_read_stopped_by_the_page_ceiling_reports_truncated()
    {
        // Advertises far more pages than the ceiling allows, so the loop runs out rather than exhausting.
        var handler = FakeHttpMessageHandler.Json(Page(1, 1, 500));
        var result = await ClientFrom(handler).QueryScenesBySiteAsync(RestBaseUrl, "token", SiteId, default);

        Assert.True(result.IsOk);
        Assert.True(result.Value!.Truncated, "a read cut short by the ceiling must not read as exhaustive");
    }

    [Fact]
    public async Task A_catalogue_read_that_reaches_the_last_page_reports_complete()
    {
        // Two pages advertised, two returned: exhausted, so nothing is withheld and the flag stays false.
        var handler = FakeHttpMessageHandler.Sequence(
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", Page(1, 1, 2)),
            FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", Page(1, 2, 2)));
        var result = await ClientFrom(handler).QueryScenesBySiteAsync(RestBaseUrl, "token", SiteId, default);

        Assert.True(result.IsOk);
        Assert.False(result.Value!.Truncated);
        Assert.Equal(2, result.Value.Scenes.Length);
    }

    [Fact]
    public async Task Full_page_under_the_last_page_is_one_call_reporting_more()
    {
        // A full page (perPage rows) that is not yet the last page is a SINGLE GET reporting another remains —
        // proving the read never runs the whole-catalogue loop. It carries the site_id filter and the Bearer
        // token and stays a read GET (no mutation).
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 3, currentPage: 1, lastPage: 5));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageBySiteAsync(RestBaseUrl, "token", SiteId, page: 1, perPage: 3, default);

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value!.Scenes.Length);
        Assert.True(result.Value.HasMore);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Contains("site_id=247", handler.LastRequest.RequestUri!.Query, StringComparison.Ordinal);
        Assert.True(handler.LastRequest.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Short_page_reports_no_more()
    {
        // Fewer rows than perPage is the end of the catalogue even when meta claims a later last_page — a short
        // page is the reliable terminator (the count is a placeholder for a gated query).
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 2, currentPage: 1, lastPage: 9));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageBySiteAsync(RestBaseUrl, "token", SiteId, page: 1, perPage: 3, default);

        Assert.True(result.IsOk);
        Assert.Equal(2, result.Value!.Scenes.Length);
        Assert.False(result.Value.HasMore);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Full_final_page_reports_no_more()
    {
        // A full page whose page reaches meta.last_page is the last one — no phantom extra round-trip.
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 3, currentPage: 1, lastPage: 1));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageBySiteAsync(RestBaseUrl, "token", SiteId, page: 1, perPage: 3, default);

        Assert.True(result.IsOk);
        Assert.False(result.Value!.HasMore);
    }

    [Fact]
    public async Task Bad_token_propagates_without_throwing()
    {
        // A 401 is the classified BadKey state, never an exception escaping the boundary — a bad token is not an
        // empty catalogue.
        var client = ClientFrom(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized));

        var result = await client.QueryScenesPageBySiteAsync(RestBaseUrl, "bad", SiteId, page: 1, perPage: 3, default);

        Assert.False(result.IsOk);
        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task Non_json_body_propagates_without_throwing()
    {
        // A reverse-proxy HTML page classifies as NotWhisparr before the parser — a source outage stays a
        // classified non-Ok state, never a silently empty page that would read as own-everything.
        var client = ClientFrom(FakeHttpMessageHandler.Html(HttpStatusCode.BadGateway));

        var result = await client.QueryScenesPageBySiteAsync(RestBaseUrl, "token", SiteId, page: 1, perPage: 3, default);

        Assert.False(result.IsOk);
        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
    }

    [Fact]
    public async Task Performer_page_targets_the_per_entity_path_carrying_paging_and_the_bearer_token()
    {
        // ThePornDB's performer sub-resource takes the canonical id as a PATH SEGMENT; no site_id filter appears.
        // The escaped id, the paging params, and the Bearer header all ride the same spine.
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 2, currentPage: 1, lastPage: 1));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageByPerformerAsync(
            RestBaseUrl, "token", PerformerId, page: 2, perPage: 50, default);

        Assert.True(result.IsOk);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal($"/performers/{PerformerId}/scenes", handler.LastRequest.RequestUri!.AbsolutePath);
        Assert.Contains("per_page=50", handler.LastRequest.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("page=2", handler.LastRequest.RequestUri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("site_id", handler.LastRequest.RequestUri.Query, StringComparison.Ordinal);
        Assert.True(handler.LastRequest.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Performer_full_page_under_the_last_page_reports_more()
    {
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 3, currentPage: 1, lastPage: 4));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageByPerformerAsync(
            RestBaseUrl, "token", PerformerId, page: 1, perPage: 3, default);

        Assert.True(result.IsOk);
        Assert.True(result.Value!.HasMore);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Performer_short_page_reports_no_more()
    {
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 1, currentPage: 1, lastPage: 9));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageByPerformerAsync(
            RestBaseUrl, "token", PerformerId, page: 1, perPage: 3, default);

        Assert.True(result.IsOk);
        Assert.False(result.Value!.HasMore);
    }

    [Fact]
    public async Task Performer_full_read_paginates_to_the_last_page()
    {
        // The whole-catalogue performer read loops on meta.last_page exactly as the site read does.
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 2, currentPage: 1, lastPage: 3));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesByPerformerAsync(RestBaseUrl, "token", PerformerId, default);

        Assert.True(result.IsOk);
        Assert.Equal(6, result.Value!.Scenes.Length);
        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task Performer_bad_token_propagates_without_throwing()
    {
        var client = ClientFrom(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized));

        var result = await client.QueryScenesPageByPerformerAsync(
            RestBaseUrl, "bad", PerformerId, page: 1, perPage: 3, default);

        Assert.False(result.IsOk);
        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task Tag_page_filters_scenes_by_the_bracketed_numeric_key_with_paging_and_the_token()
    {
        // The tag criterion is the /scenes collection plus a Laravel object-map param: the numeric id is the KEY.
        // The source requires a non-empty map value but ignores its content; a fixed placeholder rides along.
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 2, currentPage: 1, lastPage: 1));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageByTagAsync(
            RestBaseUrl, "token", NumericTagId, page: 3, perPage: 25, default);

        Assert.True(result.IsOk);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("/scenes", handler.LastRequest.RequestUri!.AbsolutePath);

        var query = Uri.UnescapeDataString(handler.LastRequest.RequestUri.Query);
        Assert.Contains($"tags[{NumericTagId}]=", query, StringComparison.Ordinal);
        Assert.Contains("per_page=25", query, StringComparison.Ordinal);
        Assert.Contains("page=3", query, StringComparison.Ordinal);
        Assert.DoesNotContain("site_id", query, StringComparison.Ordinal);
        Assert.True(handler.LastRequest.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Tag_short_page_reports_no_more_even_when_the_advertised_last_page_is_far_ahead()
    {
        // The source caps a tag filter's advertised total at 10,000 (so last_page is fabricated). A short page must
        // still end the read — trusting last_page here would claim thousands of phantom scenes.
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 2, currentPage: 1, lastPage: 100));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageByTagAsync(
            RestBaseUrl, "token", NumericTagId, page: 1, perPage: 100, default);

        Assert.True(result.IsOk);
        Assert.False(result.Value!.HasMore);
    }

    [Fact]
    public async Task Tag_full_page_under_the_last_page_reports_more()
    {
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 3, currentPage: 1, lastPage: 5));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageByTagAsync(
            RestBaseUrl, "token", NumericTagId, page: 1, perPage: 3, default);

        Assert.True(result.IsOk);
        Assert.True(result.Value!.HasMore);
    }

    [Fact]
    public async Task A_site_page_composes_every_query_axis_into_one_scenes_url()
    {
        // All four axes compose in ONE url, which is why a fully filtered page costs what an unfiltered one
        // costs. The entity's own fragment leads and is always present — a criterion without it would enumerate
        // the whole collection.
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 2, currentPage: 1, lastPage: 1));

        var result = await ClientFrom(handler).QueryScenesPageBySiteAsync(
            RestBaseUrl, "token", SiteId, page: 1, perPage: 40, default,
            new TpdbClient.TpdbSceneFilter(PerformerId: "289008", TagId: "29", Year: 2019));

        Assert.True(result.IsOk);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal("/scenes", handler.LastRequest!.RequestUri!.AbsolutePath);

        var query = Uri.UnescapeDataString(handler.LastRequest.RequestUri.Query);
        Assert.StartsWith($"?site_id={SiteId}", query, StringComparison.Ordinal);
        Assert.Contains("performers[289008]=1", query, StringComparison.Ordinal);
        Assert.Contains("tags[29]=1", query, StringComparison.Ordinal);
        Assert.Contains("year=2019", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_collection_route_performer_read_keys_on_the_numeric_id_it_was_given()
    {
        // The re-route: /performers/{uuid}/scenes ignores every query parameter (verified live — five of them
        // each left the total unchanged at 1168). A filtered performer read must go through the collection
        // keyed on the canonical NUMERIC id.
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 2, currentPage: 1, lastPage: 1));

        await ClientFrom(handler).QueryScenesPageByPerformerCollectionAsync(
            RestBaseUrl, "token", "289008", page: 1, perPage: 40,
            new TpdbClient.TpdbSceneFilter(Year: 2024), default);

        Assert.Equal("/scenes", handler.LastRequest!.RequestUri!.AbsolutePath);
        var query = Uri.UnescapeDataString(handler.LastRequest.RequestUri.Query);
        Assert.StartsWith("?performers[289008]=1", query, StringComparison.Ordinal);
        Assert.Contains("year=2024", query, StringComparison.Ordinal);
        Assert.DoesNotContain(PerformerId, query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tag_name_resolves_to_the_single_exact_match_ignoring_fuzzier_rows()
    {
        // /tags?q= is a FUZZY relevance search: the first row is not the answer. Without exact
        // (case-insensitive) name matching a tag would silently read another tag's catalogue.
        var handler = FakeHttpMessageHandler.Json("""
            {"data":[
              {"id":9001,"name":"Anal Masturbation"},
              {"id":70,"name":"anal"},
              {"id":9002,"name":"Anal Squirting"}
            ]}
            """);
        var client = ClientFrom(handler);

        var result = await client.ResolveTagIdByNameAsync(RestBaseUrl, "token", "Anal", default);

        Assert.True(result.IsOk);
        Assert.Equal("70", result.Value);
        Assert.Equal("/tags", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("q=Anal", handler.LastRequest.RequestUri.Query, StringComparison.Ordinal);
        Assert.True(handler.LastRequest.Headers.Contains("Authorization"));
    }

    [Fact]
    public async Task Tag_name_with_no_exact_match_resolves_to_null()
    {
        // The source knows the concept under a different label (e.g. "Blonde Hair (Female)" for "Blonde"). No
        // exact match means no id, and the caller shows its honest no-id state.
        var client = ClientFrom(FakeHttpMessageHandler.Json("""
            {"data":[{"id":136186,"name":"Blonde Hair (Male)"},{"id":147793,"name":"Blonde Hair (Female)"}]}
            """));

        var result = await client.ResolveTagIdByNameAsync(RestBaseUrl, "token", "Blonde", default);

        Assert.True(result.IsOk);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Tag_name_matching_several_rows_resolves_to_null_rather_than_guessing()
    {
        var client = ClientFrom(FakeHttpMessageHandler.Json("""
            {"data":[{"id":1,"name":"Gaping"},{"id":2,"name":"gaping"}]}
            """));

        var result = await client.ResolveTagIdByNameAsync(RestBaseUrl, "token", "Gaping", default);

        Assert.True(result.IsOk);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task Tag_name_lookup_classifies_a_non_ok_upstream_instead_of_throwing()
    {
        var client = ClientFrom(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized));

        var result = await client.ResolveTagIdByNameAsync(RestBaseUrl, "bad", "Anal", default);

        Assert.False(result.IsOk);
        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task Tag_bad_token_propagates_without_throwing()
    {
        var client = ClientFrom(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized));

        var result = await client.QueryScenesPageByTagAsync(
            RestBaseUrl, "bad", NumericTagId, page: 1, perPage: 3, default);

        Assert.False(result.IsOk);
        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }
}
