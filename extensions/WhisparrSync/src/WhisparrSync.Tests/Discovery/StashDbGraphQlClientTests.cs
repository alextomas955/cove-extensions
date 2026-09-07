using System.Net;
using System.Text.Json;
using WhisparrSync.Client;
using WhisparrSync.Discovery;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Discovery;

/// <summary>
/// The offline lock for the single-page StashDB read (the incremental Missing tab's page fetch): host-free, the
/// transport driven by a <see cref="FakeHttpMessageHandler"/>. It proves the read issues EXACTLY ONE upstream
/// POST per page (never the full pagination loop), the hasMore terminator (full page under the count vs a short
/// or final page), and the classify-not-throw contract on a non-Ok upstream.
/// </summary>
[Trait("Tier", "L0")]
public sealed class StashDbGraphQlClientTests
{
    private const string Endpoint = "https://stashdb.org/graphql";
    private const string StudioId = "be4be46f-692f-4509-ba23-90a96abf0b16";
    private const string ChildStudioIdA = "11111111-1111-4111-8111-111111111111";
    private const string ChildStudioIdB = "22222222-2222-4222-8222-222222222222";
    private const string TagId = "7bded732-133c-4007-b4d1-2fe08d01dabc";
    private const string PerformerId = "3c1a5f80-9d2b-4a55-9e10-0f5b2c7ad311";

    private static StashDbGraphQlClient ClientFrom(FakeHttpMessageHandler handler)
        => new(new HttpClient(handler));

    // A queryScenes 200 body carrying sceneCount rows and an advertised catalogue count.
    private static string Page(int sceneCount, int count)
    {
        var scenes = string.Join(",", Enumerable.Range(0, sceneCount).Select(i =>
            $"{{\"id\":\"scene-{i}\",\"title\":\"Scene {i}\",\"release_date\":null,\"studio\":null,\"images\":null}}"));
        return $"{{\"data\":{{\"queryScenes\":{{\"count\":{count},\"scenes\":[{scenes}]}}}}}}";
    }

    [Fact]
    public async Task Full_page_under_the_count_is_one_call_reporting_more()
    {
        // A full page (perPage rows) with a large advertised count is a SINGLE POST that reports another page
        // remains — proving the read never runs the whole-catalogue loop. It stays a read query (no mutation),
        // authed by the ApiKey header (not X-Api-Key).
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 3, count: 100));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageByStudioAsync(Endpoint, "key", [StudioId], page: 1, perPage: 3, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value!.Scenes.Length);
        Assert.Equal(100, result.Value.Count);
        Assert.True(result.Value.HasMore);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("queryScenes", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation", handler.LastRequestBody!, StringComparison.OrdinalIgnoreCase);
        Assert.True(handler.LastRequest.Headers.Contains("ApiKey"));
    }

    [Fact]
    public async Task Short_page_reports_no_more()
    {
        // Fewer rows than perPage is the last page even when the advertised count is not yet covered (a
        // stale/estimated count must not force an extra empty round-trip).
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 2, count: 2));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageByStudioAsync(Endpoint, "key", [StudioId], page: 1, perPage: 3, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(2, result.Value!.Scenes.Length);
        Assert.False(result.Value.HasMore);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Full_final_page_reports_no_more()
    {
        // A full page whose page*perPage reaches the advertised count is the last page — no extra empty round-trip.
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 3, count: 3));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageByStudioAsync(Endpoint, "key", [StudioId], page: 1, perPage: 3, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.False(result.Value!.HasMore);
    }

    [Fact]
    public async Task Tag_page_read_carries_the_tags_criterion_and_no_studios_or_performers()
    {
        // The tag page read builds queryScenes(tags INCLUDES [tagId]) — the tag analogue of the studio/performer
        // page reads, differing ONLY in the filter criterion. The sent body carries the tags criterion + the tag
        // id, never a studios/performers criterion, and stays a read query. (The quoted "tags"/"studios"/
        // "performers" target the input criterion in variables, not the selection set, whose tags/performers
        // fields are unquoted.)
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 3, count: 100));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageByTagAsync(Endpoint, "key", [TagId], page: 1, perPage: 3, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Contains("\"tags\"", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains(TagId, handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.Contains("INCLUDES", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"studios\"", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"performers\"", handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation", handler.LastRequestBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Studio_page_read_unions_parent_and_child_ids_in_one_includes_criterion()
    {
        // A parent studio's read passes its own id plus every child sub-studio's; the client folds the whole set
        // into ONE studios INCLUDES criterion sent in a SINGLE POST — the aggregation is one union query, never
        // one query per child (no N+1). The full id array rides the wire in order, and it stays a read query.
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 3, count: 100));
        var client = ClientFrom(handler);

        var result = await client.QueryScenesPageByStudioAsync(
            Endpoint, "key", [StudioId, ChildStudioIdA, ChildStudioIdB], page: 1, perPage: 3, DiscoveryQuery.Default, default);

        Assert.True(result.IsOk);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains(
            $"\"studios\":{{\"value\":[\"{StudioId}\",\"{ChildStudioIdA}\",\"{ChildStudioIdB}\"],\"modifier\":\"INCLUDES\"}}",
            handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation", handler.LastRequestBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Single_id_studio_read_emits_the_byte_identical_single_id_criterion()
    {
        // The non-parent regression lock: a lone studio id emits studios INCLUDES ["id"] with exactly one element
        // in the criterion array — byte-for-byte what a single-studio read has always sent. The union threading
        // never widened a childless studio's read (no stray child id leaks in).
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 3, count: 100));
        var client = ClientFrom(handler);

        await client.QueryScenesPageByStudioAsync(Endpoint, "key", [StudioId], page: 1, perPage: 3, DiscoveryQuery.Default, default);

        Assert.Equal(1, handler.CallCount);
        Assert.Contains(
            $"\"studios\":{{\"value\":[\"{StudioId}\"],\"modifier\":\"INCLUDES\"}}",
            handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain(ChildStudioIdA, handler.LastRequestBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task All_four_filters_and_the_sort_ride_one_scene_query_input()
    {
        // Every criterion ANDs into the SAME queryScenes input, which is why a facet selection costs no extra
        // provider call. Asserted against the serialized body, the only place the provider reads them from.
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 3, count: 100));

        await ClientFrom(handler).QueryScenesPageByStudioAsync(
            Endpoint, "key", [StudioId], page: 1, perPage: 3,
            new DiscoveryQuery(DiscoverySortMode.Oldest, null, PerformerId, TagId, 2016), default);

        var body = handler.LastRequestBody!;
        Assert.Contains($"\"studios\":{{\"value\":[\"{StudioId}\"]", body, StringComparison.Ordinal);
        Assert.Contains($"\"performers\":{{\"value\":[\"{PerformerId}\"]", body, StringComparison.Ordinal);
        Assert.Contains($"\"tags\":{{\"value\":[\"{TagId}\"]", body, StringComparison.Ordinal);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task The_year_bound_follows_the_requested_ordering_and_is_never_an_equality()
    {
        // DateCriterionInput takes one value and one modifier, and EQUALS on "2021" and on "2021-01-01" both
        // returned count: 0 with no error (verified live). A descending date order takes the upper bound, which
        // puts the wanted year at the head of the result; the other two orderings take the lower one.
        var newest = await SentInput(DiscoveryQuery.Default with { Year = 2016 });
        Assert.Contains("\"date\":{\"value\":\"2017-01-01\",\"modifier\":\"LESS_THAN\"}", newest, StringComparison.Ordinal);

        var oldest = await SentInput(DiscoveryQuery.Default with { Sort = DiscoverySortMode.Oldest, Year = 2016 });
        Assert.Contains("\"date\":{\"value\":\"2015-12-31\",\"modifier\":\"GREATER_THAN\"}", oldest, StringComparison.Ordinal);

        var title = await SentInput(DiscoveryQuery.Default with { Sort = DiscoverySortMode.Title, Year = 2016 });
        Assert.Contains("\"date\":{\"value\":\"2015-12-31\",\"modifier\":\"GREATER_THAN\"}", title, StringComparison.Ordinal);

        foreach (var body in new[] { newest, oldest, title })
        {
            Assert.DoesNotContain("EQUALS", body, StringComparison.Ordinal);
        }

        // No year asked for means no date criterion at all, never an open bound the provider would narrow on.
        Assert.DoesNotContain("\"date\"", await SentInput(DiscoveryQuery.Default), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_filter_does_not_move_where_pagination_stops()
    {
        // Invariant B under a filter: hasMore derives SHORT-PAGE-FIRST, and a filter that shrinks the advertised
        // count changes nothing about where the read ends.
        var filtered = DiscoveryQuery.Default with { TagFilterId = TagId, Year = 2016 };

        // A short page ends the read even while the count claims a great deal more remains.
        var shortPage = FakeHttpMessageHandler.Json(Page(sceneCount: 2, count: 900));
        var ended = await ClientFrom(shortPage).QueryScenesPageByStudioAsync(
            Endpoint, "key", [StudioId], page: 1, perPage: 3, filtered, default);
        Assert.False(ended.Value!.HasMore);
        Assert.Equal(1, shortPage.CallCount);

        // A full page under the count still reports another.
        var fullPage = FakeHttpMessageHandler.Json(Page(sceneCount: 3, count: 9));
        var more = await ClientFrom(fullPage).QueryScenesPageByStudioAsync(
            Endpoint, "key", [StudioId], page: 1, perPage: 3, filtered, default);
        Assert.True(more.Value!.HasMore);

        // A count left stale by the filter, larger than the real set: the following SHORT page ends the read
        // there, buying no extra empty round-trip.
        var stale = FakeHttpMessageHandler.Json(Page(sceneCount: 1, count: 9));
        var stopped = await ClientFrom(stale).QueryScenesPageByStudioAsync(
            Endpoint, "key", [StudioId], page: 2, perPage: 3, filtered, default);
        Assert.False(stopped.Value!.HasMore);
    }

    [Fact]
    public async Task The_graphql_query_string_is_byte_identical_across_different_filter_ids()
    {
        // Filter ids travel as typed source-gen JSON VARIABLES against a const query string; nothing is
        // concatenated into the operation, which is what makes injection through a filter id unreachable.
        var first = QueryOf(await SentInput(
            new DiscoveryQuery(DiscoverySortMode.Newest, null, PerformerId, TagId, 2016)));
        var second = QueryOf(await SentInput(
            new DiscoveryQuery(DiscoverySortMode.Title, null, TagId, PerformerId, 1999)));

        Assert.Equal(first, second);
        Assert.Contains("queryScenes", first, StringComparison.Ordinal);
    }

    // The serialized request body a single studio page read sends under the given query.
    private static async Task<string> SentInput(DiscoveryQuery query)
    {
        var handler = FakeHttpMessageHandler.Json(Page(sceneCount: 3, count: 100));
        await ClientFrom(handler).QueryScenesPageByStudioAsync(
            Endpoint, "key", [StudioId], page: 1, perPage: 3, query, default);
        return handler.LastRequestBody!;
    }

    private static string QueryOf(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("query").GetString()!;

    [Fact]
    public async Task Bad_key_propagates_without_throwing()
    {
        // A 401 is the classified BadKey state, never an exception escaping the boundary.
        var client = ClientFrom(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized));

        var result = await client.QueryScenesPageByStudioAsync(Endpoint, "bad", [StudioId], page: 1, perPage: 3, DiscoveryQuery.Default, default);

        Assert.False(result.IsOk);
        Assert.Equal(WhisparrResultState.BadKey, result.State);
    }

    [Fact]
    public async Task Non_json_body_propagates_without_throwing()
    {
        // A reverse-proxy HTML page classifies as NotWhisparr before the parser — the same classify-not-throw
        // path a rate-limit exhaustion returns through.
        var client = ClientFrom(FakeHttpMessageHandler.Html(HttpStatusCode.BadGateway));

        var result = await client.QueryScenesPageByStudioAsync(Endpoint, "key", [StudioId], page: 1, perPage: 3, DiscoveryQuery.Default, default);

        Assert.False(result.IsOk);
        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
    }

    // A captured studio-roster body: rowCount {id,name} rows and the advertised roster size.
    private static string Roster(int rowCount, int count)
    {
        var rows = string.Join(",", Enumerable.Range(0, rowCount).Select(i =>
            $"{{\"id\":\"performer-{i}\",\"name\":\"Performer {i}\"}}"));
        return $"{{\"data\":{{\"queryPerformers\":{{\"count\":{count},\"performers\":[{rows}]}}}}}}";
    }

    [Fact]
    public async Task The_studio_roster_parses_its_rows_and_rides_an_explicit_stable_ordering()
    {
        var handler = FakeHttpMessageHandler.Json(Roster(rowCount: 3, count: 3));

        var result = await ClientFrom(handler).QueryStudioPerformersAsync(Endpoint, "key", StudioId, default);

        Assert.True(result.IsOk);
        Assert.Equal(3, result.Value!.Count);
        Assert.Equal(
            new[] { "performer-0=Performer 0", "performer-1=Performer 1", "performer-2=Performer 2" },
            result.Value.Rows.Select(row => row.Id + "=" + row.Name));

        // ONE request, no loop and no page walk — and the ordering is on the wire, which is what makes a single
        // ordered page a complete roster and not an arbitrary sample of one.
        Assert.Equal(1, handler.CallCount);
        var input = JsonDocument.Parse(handler.LastRequestBody!).RootElement
            .GetProperty("variables").GetProperty("input");
        Assert.Equal("NAME", input.GetProperty("sort").GetString());
        Assert.Equal("ASC", input.GetProperty("direction").GetString());
        Assert.Equal(StudioId, input.GetProperty("studio_id").GetString());
        Assert.Equal(1, input.GetProperty("page").GetInt32());
        Assert.True(input.GetProperty("per_page").GetInt32() >= 1000);
        Assert.DoesNotContain("mutation", handler.LastRequestBody!, StringComparison.OrdinalIgnoreCase);
        Assert.True(handler.LastRequest!.Headers.Contains("ApiKey"));
    }

    [Fact]
    public async Task A_roster_larger_than_the_page_reports_the_count_it_could_not_return()
    {
        // The truncation signal: rows short of count is the ONLY way a caller can tell a prefix from a roster.
        var result = await ClientFrom(FakeHttpMessageHandler.Json(Roster(rowCount: 2, count: 1696)))
            .QueryStudioPerformersAsync(Endpoint, "key", StudioId, default);

        Assert.True(result.IsOk);
        Assert.Equal(1696, result.Value!.Count);
        Assert.Equal(2, result.Value.Rows.Length);
    }

    [Fact]
    public async Task The_performer_studio_list_parses_through_its_join_node()
    {
        var handler = FakeHttpMessageHandler.Json(
            "{\"data\":{\"findPerformer\":{\"studios\":["
            + "{\"studio\":{\"id\":\"studio-a\",\"name\":\"Studio A\"}},"
            + "{\"studio\":{\"id\":\"studio-b\",\"name\":\"Studio B\"}}]}}}");

        var result = await ClientFrom(handler).QueryPerformerStudiosAsync(Endpoint, "key", PerformerId, default);

        Assert.True(result.IsOk);
        Assert.Equal(
            new[] { "studio-a=Studio A", "studio-b=Studio B" },
            result.Value!.Select(row => row.Id + "=" + row.Name));
        Assert.Equal(1, handler.CallCount);
        Assert.Contains(PerformerId, handler.LastRequestBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("mutation", handler.LastRequestBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_performer_the_source_does_not_know_yields_an_empty_studio_list()
    {
        var result = await ClientFrom(FakeHttpMessageHandler.Json("{\"data\":{\"findPerformer\":null}}"))
            .QueryPerformerStudiosAsync(Endpoint, "key", PerformerId, default);

        Assert.True(result.IsOk);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public async Task A_non_ok_aggregate_propagates_rather_than_reading_as_an_empty_option_list()
    {
        // An empty option list and an unreachable provider must not be the same answer: the caller decides the
        // fallback, and it can only decide it if the failure survives the transport.
        var badKey = await ClientFrom(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized))
            .QueryStudioPerformersAsync(Endpoint, "bad", StudioId, default);
        Assert.False(badKey.IsOk);
        Assert.Equal(WhisparrResultState.BadKey, badKey.State);

        var unreachable = await ClientFrom(FakeHttpMessageHandler.Html(HttpStatusCode.BadGateway))
            .QueryStudioPerformersAsync(Endpoint, "key", StudioId, default);
        Assert.False(unreachable.IsOk);
        Assert.Equal(WhisparrResultState.NotWhisparr, unreachable.State);

        var studios = await ClientFrom(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized))
            .QueryPerformerStudiosAsync(Endpoint, "bad", PerformerId, default);
        Assert.False(studios.IsOk);
        Assert.Equal(WhisparrResultState.BadKey, studios.State);
    }

    [Fact]
    public async Task A_graphql_error_only_body_is_classified_rather_than_read_as_an_empty_roster()
    {
        var result = await ClientFrom(FakeHttpMessageHandler.Json("{\"errors\":[{\"message\":\"boom\"}]}"))
            .QueryStudioPerformersAsync(Endpoint, "key", StudioId, default);

        Assert.False(result.IsOk);
        Assert.Equal(WhisparrResultState.NotWhisparr, result.State);
    }
}
