using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Providers;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Missing;

/// <summary>
/// The whole-catalogue marking route: which entities it expresses, what it enqueues, what the run
/// then marks, and that none of it downloads.
/// </summary>
/// <remarks>
/// Driven through the shipped registration and the shipped derivation. A run compared against a set
/// this file composed for it would agree with whatever it was given, and the claim under test is
/// that the run marks the set the grid shows.
/// </remarks>
public sealed class MissingMonitorAllTests
{
    private const string MonitorAll = "missing/monitor-all";

    private const string StashDb = "https://stashdb.org/graphql";

    /// <summary>Two pages and a bit, at the page size the surface reads in.</summary>
    private const int Catalogue = 85;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>A host whose configured metadata source really answers a catalogue.</summary>
    private static Task<MonitorHost> HostOverAsync(PagedProviderCatalogue catalogue)
    {
        var config = new CoveConfiguration();
        config.Scraping.MetadataServers.Add(
            new MetadataServerInstance
            {
                Endpoint = StashDb,
                ApiKey = "a-key",
                Name = "stashdb",
                MaxRequestsPerMinute = 0,
            });

        return MonitorHost.CreateAsync(catalogue: catalogue, metadataConfig: config);
    }

    /// <summary>A catalogue where a title search leaves a strict subset.</summary>
    private static PagedProviderCatalogue PagedCatalogue()
        => new(
            [.. Enumerable.Range(0, Catalogue).Select(at => SceneNamed(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{(at % 3 == 0 ? "pool" : "room")}-scene-{at}")))],
            perPage: 40);

    private static ProviderScene SceneNamed(string id)
        => new(id, id, null, null, null, null, [], []);

    private static async Task<MissingBulkEnqueued> MonitorAllAsync(
        MonitorHost host, string kind, int coveId, string? query = null)
    {
        var answered = await host.Http.PostAsync(
            host.RouteFor(kind, coveId, MonitorAll) + (query is null ? string.Empty : "?" + query),
            content: null,
            TestCt);

        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<MissingBulkEnqueued>(TestCt))!;
    }

    /// <summary>Every scene the grid draws for one narrowing, over every page it serves.</summary>
    private static async Task<List<string>> GridScenesAsync(
        MonitorHost host, string kind, int coveId, string? query)
    {
        var drawn = new List<string>();
        var page = 1;
        while (true)
        {
            var route = string.Create(
                CultureInfo.InvariantCulture,
                $"{host.RouteFor(kind, coveId, "missing")}?page={page}");
            var answered = await host.Http.GetAsync(
                query is null ? route : route + "&" + query, TestCt);
            answered.EnsureSuccessStatusCode();
            var view = (await answered.Content.ReadFromJsonAsync<MissingPageView>(TestCt))!;

            drawn.AddRange(view.Cards.Select(card => card.ProviderSceneId));
            if (page >= view.LastPage)
            {
                return drawn;
            }

            page++;
        }
    }

    /// <summary>The scenes the last enqueued run offered the instance, in order.</summary>
    private static async Task<List<string>> RunAndReadOfferedAsync(MonitorHost host)
    {
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        return [.. host.Client.Acting
            .Where(call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync))
            .Select(call => call.ForeignId!)];
    }

    /// <summary>
    /// The route answers a job id without waiting for the run, against a run that never starts.
    /// </summary>
    [Fact]
    public async Task TheRouteAnswersAJobIdWithoutWaitingForTheRun()
    {
        await using var host = await HostOverAsync(PagedCatalogue());
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var enqueued = await MonitorAllAsync(host, "studio", studioId);

        Assert.NotNull(enqueued.JobId);
        Assert.Equal(MissingRefusalKind.None, enqueued.Refusal);
        Assert.Empty(host.Client.Acting);
    }

    /// <summary>The enqueued type carries this extension's own prefix and the run's own id.</summary>
    [Fact]
    public async Task TheEnqueuedTypeCarriesTheExtensionsOwnPrefixAndIsExclusive()
    {
        await using var host = await HostOverAsync(PagedCatalogue());
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        await MonitorAllAsync(host, "studio", studioId);

        var job = Assert.Single(host.Jobs.Enqueued);
        Assert.StartsWith("ext:" + host.ExtensionId + ":", job.Type, StringComparison.Ordinal);
        Assert.EndsWith(MissingMonitorAllJob.JobId, job.Type, StringComparison.Ordinal);
        Assert.True(job.Exclusive);
    }

    /// <summary>
    /// A performer is expressed too, so the refusal below is about the tag rather than about
    /// everything that is not a studio.
    /// </summary>
    [Fact]
    public async Task APerformerIsOfferedTheRunAsWell()
    {
        await using var host = await HostOverAsync(PagedCatalogue());
        var performerId = await host.SeedPerformerAsync(
            MonitorHost.StoredEndpoint, MonitorHost.PerformerRemoteIdValue);

        var enqueued = await MonitorAllAsync(host, "performer", performerId);

        Assert.NotNull(enqueued.JobId);
        Assert.Single(host.Jobs.Enqueued);
    }

    /// <summary>
    /// A tag is refused at the route, not merely left undrawn in the browser.
    /// </summary>
    /// <remarks>
    /// The control is absent on a tag page because a tag's catalogue spans the library. An absent
    /// control is not a bound: the address is reachable without one, so the route has to express no
    /// tag rather than trust that nothing sends it.
    /// </remarks>
    [Fact]
    public async Task ATagIsRefusedAndNothingIsEnqueued()
    {
        await using var host = await HostOverAsync(PagedCatalogue());
        var tagId = await host.SeedTagAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var answered = await host.Http.PostAsync(
            host.RouteFor("tag", tagId, MonitorAll), content: null, TestCt);

        Assert.Equal(HttpStatusCode.BadRequest, answered.StatusCode);
        Assert.Empty(host.Jobs.Enqueued);
        Assert.Empty(host.Client.Acting);
    }

    /// <summary>
    /// The run offers exactly the scenes the grid draws for the same narrowing, over every page.
    /// </summary>
    /// <remarks>
    /// Both sides are read from the shipped surfaces: the grid's from its own route, the run's from
    /// what actually reached the instance. That is the whole claim of the feature, the browser having
    /// sent no identifier at all.
    /// </remarks>
    [Fact]
    public async Task TheRunOffersTheScenesTheGridDrawsForTheSameNarrowing()
    {
        await using var host = await HostOverAsync(PagedCatalogue());
        host.Client.Answering(nameof(RecordingWhisparrClient.AddSceneAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrClient.ReadSceneByRemoteIdAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrClient.ReadEntityPresenceAsync), MonitorHost.Json(200, "{}"));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        var drawn = await GridScenesAsync(host, "studio", studioId, "q=pool");
        await MonitorAllAsync(host, "studio", studioId, "q=pool");
        var offered = await RunAndReadOfferedAsync(host);

        Assert.Equal(drawn, offered);

        // The narrowing has to leave a strict subset, or a run over everything would pass this.
        Assert.NotEmpty(drawn);
        Assert.True(drawn.Count < Catalogue, "the search narrowed nothing");
    }

    /// <summary>An unnarrowed run walks every page of the catalogue.</summary>
    [Fact]
    public async Task AnUnnarrowedRunReachesEveryPage()
    {
        await using var host = await HostOverAsync(PagedCatalogue());
        host.Client.Answering(nameof(RecordingWhisparrClient.AddSceneAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrClient.ReadSceneByRemoteIdAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrClient.ReadEntityPresenceAsync), MonitorHost.Json(200, "{}"));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        await MonitorAllAsync(host, "studio", studioId);
        var offered = await RunAndReadOfferedAsync(host);

        Assert.Equal(Catalogue, offered.Count);
        Assert.Equal(Catalogue, offered.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The run marks and never grabs.
    /// </summary>
    /// <remarks>
    /// Asserted as the set of verbs the run used rather than as the absence of one name, so a
    /// grabbing verb added to the seam and then reached is a failure here rather than an omission
    /// from a list nobody updated.
    /// </remarks>
    [Fact]
    public async Task TheRunIssuesTheSceneAddAndNoOtherVerb()
    {
        await using var host = await HostOverAsync(PagedCatalogue());
        host.Client.Answering(nameof(RecordingWhisparrClient.AddSceneAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrClient.ReadSceneByRemoteIdAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrClient.ReadEntityPresenceAsync), MonitorHost.Json(200, "{}"));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        await MonitorAllAsync(host, "studio", studioId);
        host.Client.Verbs.Clear();
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        Assert.Equal(
            [
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                nameof(IWhisparrClient.ReadQualityProfilesAsync),
                nameof(IWhisparrClient.ReadRootFoldersAsync),
            ],
            host.Client.Verbs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

    /// <summary>A run round-trips its entity and its narrowing across the host's parameter map.</summary>
    [Fact]
    public void ARunRoundTripsItsEntityAndItsNarrowing()
    {
        var decoded = MissingMonitorAllJob.Decode(
            MissingMonitorAllJob.Encode(
                WhisparrEntityKind.Performer, 41, "pool", "year:2024"));

        Assert.Equal(WhisparrEntityKind.Performer, decoded.Kind);
        Assert.Equal(41, decoded.CoveId);
        Assert.Equal("pool", decoded.TitleSearch);
        Assert.Equal("year:2024", decoded.Filters);
    }

    /// <summary>
    /// A map nothing can be read out of names no kind rather than the first one declared.
    /// </summary>
    [Fact]
    public void AMapNothingCanBeReadOutOfNamesNoKind()
    {
        Assert.Null(MissingMonitorAllJob.Decode(null).Kind);
        Assert.Null(
            MissingMonitorAllJob.Decode(
                new Dictionary<string, string>(StringComparer.Ordinal)).Kind);
        Assert.Null(
            MissingMonitorAllJob.Decode(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["kind"] = "episode" })
                .Kind);
    }

    /// <summary>A narrowing nobody asked for reads back as none rather than as an empty one.</summary>
    [Fact]
    public void AnUnnarrowedRunCarriesNoNarrowing()
    {
        var decoded = MissingMonitorAllJob.Decode(
            MissingMonitorAllJob.Encode(WhisparrEntityKind.Studio, 7, null, "  "));

        Assert.Null(decoded.TitleSearch);
        Assert.Null(decoded.Filters);
    }
}
