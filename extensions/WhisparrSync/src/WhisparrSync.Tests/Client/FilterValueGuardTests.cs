using System.Net;
using WhisparrSync.Client;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Client;

/// <summary>
/// Every read that interpolates a caller-supplied value into a filter or a path segment must refuse a
/// blank one before building the request. Whisparr treats an empty filter as NO filter and answers with
/// the entire set, and an empty path segment collapses the URL onto the collection route — so a value
/// that was never set reads the whole library instead of failing, on a library of any size.
/// </summary>
/// <remarks>
/// The load-bearing assertion is the outbound request COUNT, not the shape of the answer: a read that
/// reached Whisparr and then discarded the result would still have paid for the whole set.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class FilterValueGuardTests
{
    private const string BaseUrl = "http://localhost:6969";
    private const string ApiKey = "test-api-key";

    private static IEnumerable<(string Name, Func<WhisparrClient, string, Task<WhisparrResultState>> Invoke)> FilterCarryingReads()
    {
        yield return (nameof(WhisparrClient.GetMovieByStashIdAsync),
            async (c, v) => (await c.GetMovieByStashIdAsync(BaseUrl, ApiKey, v, default)).State);
        yield return (nameof(WhisparrClient.GetStudioByStashIdAsync),
            async (c, v) => (await c.GetStudioByStashIdAsync(BaseUrl, ApiKey, v, default)).State);
        yield return (nameof(WhisparrClient.GetPerformerByStashIdAsync),
            async (c, v) => (await c.GetPerformerByStashIdAsync(BaseUrl, ApiKey, v, default)).State);
        yield return (nameof(WhisparrClient.ListManualImportAsync),
            async (c, v) => (await c.ListManualImportAsync(BaseUrl, ApiKey, v, default)).State);
        yield return (nameof(WhisparrClient.LookupSeriesAsync),
            async (c, v) => (await c.LookupSeriesAsync(BaseUrl, ApiKey, v, default)).State);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public async Task A_blank_value_costs_nothing_and_answers_non_Ok(string blank)
    {
        foreach (var (name, invoke) in FilterCarryingReads())
        {
            var handler = FakeHttpMessageHandler.Json("[]");
            var state = await invoke(new WhisparrClient(new HttpClient(handler)), blank);

            var total = WhisparrRequestCounter.Classify(handler).Total;
            Assert.True(total == 0, $"{name} issued {total} outbound request(s) for a blank value");
            Assert.True(state != WhisparrResultState.Ok, $"{name} answered Ok for a blank value");
        }
    }

    [Fact]
    public async Task A_real_value_still_issues_exactly_one_request()
    {
        foreach (var (name, invoke) in FilterCarryingReads())
        {
            var handler = FakeHttpMessageHandler.Json("[]");
            await invoke(new WhisparrClient(new HttpClient(handler)), "a-real-value");

            var total = WhisparrRequestCounter.Classify(handler).Total;
            Assert.True(total == 1, $"{name} issued {total} outbound request(s) for a real value");
        }
    }

    [Fact]
    public async Task A_blank_value_is_classified_as_the_refusal_state()
    {
        foreach (var (name, invoke) in FilterCarryingReads())
        {
            var state = await invoke(new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json("[]"))), "");
            Assert.True(state == WhisparrResultState.NotAsked, $"{name} answered {state} for a blank value");
        }
    }

    [Fact]
    public void The_refusal_discriminator_cannot_be_read_as_an_outage_or_a_data_outcome()
    {
        var refusal = Ext.FailureDiscriminator(WhisparrResultState.NotAsked);

        foreach (var state in Enum.GetValues<WhisparrResultState>())
        {
            if (state != WhisparrResultState.NotAsked)
            {
                Assert.NotEqual(refusal, Ext.FailureDiscriminator(state));
            }
        }
    }

    [Fact]
    public async Task A_real_value_still_classifies_exactly_as_before()
    {
        var movies = await new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Json("[]")))
            .GetMovieByStashIdAsync(BaseUrl, ApiKey, "a-real-value", default);
        Assert.True(movies.IsOk);
        Assert.Empty(movies.Value!);

        var badKey = await new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Status(HttpStatusCode.Unauthorized)))
            .GetMovieByStashIdAsync(BaseUrl, ApiKey, "a-real-value", default);
        Assert.Equal(WhisparrResultState.BadKey, badKey.State);

        // The one wire nuance the guard must not disturb: a not-added performer's 404 stays a data outcome.
        var absent = await new WhisparrClient(new HttpClient(FakeHttpMessageHandler.Status(HttpStatusCode.NotFound)))
            .GetPerformerByStashIdAsync(BaseUrl, ApiKey, "a-real-value", default);
        Assert.Equal(WhisparrResultState.Absent, absent.State);
    }
}
