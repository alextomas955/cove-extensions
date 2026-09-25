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

public sealed class MissingMonitorAllTests
{
    private const string MonitorAll = "missing/monitor-all";

    private const string StashDb = "https://stashdb.org/graphql";

    // Two pages and a bit at the page size the surface reads in.
    private const int Catalogue = 85;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // The instance's own list is what a run walks now, so the host is given the same scenes the
    // paged source stands for.
    private static async Task<MonitorHost> HostOverAsync(PagedProviderCatalogue catalogue)
    {
        var host = await HostWithProviderAsync(catalogue);
        host.Client.EntityCatalogues[MonitorHost.StudioRemoteIdValue] =
        [
            .. CatalogueSceneIds().Select(
                id => new WhisparrCatalogueScene(
                    id, id, null, null, null, null, [], [], Monitored: false, HasFile: false)),
        ];
        return host;
    }

    private static IEnumerable<string> CatalogueSceneIds()
        => Enumerable.Range(0, Catalogue).Select(at => string.Create(
            CultureInfo.InvariantCulture,
            $"{(at % 3 == 0 ? "pool" : "room")}-scene-{at}"));

    private static Task<MonitorHost> HostWithProviderAsync(PagedProviderCatalogue catalogue)
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

    private static PagedProviderCatalogue PagedCatalogue()
        => new([.. CatalogueSceneIds().Select(SceneNamed)], perPage: 40);

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

    private static async Task<List<string>> RunAndReadOfferedAsync(MonitorHost host)
    {
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        return [.. host.Client.Acting
            .Where(call => call.Verb == nameof(IWhisparrMissingSceneActing.AddSceneAsync))
            .Select(call => call.ForeignId!)];
    }

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

    [Fact]
    public async Task TheRunOffersTheScenesTheGridDrawsForTheSameNarrowing()
    {
        await using var host = await HostOverAsync(PagedCatalogue());
        host.Client.Answering(nameof(RecordingWhisparrCore.AddSceneAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrCore.ReadSceneByRemoteIdAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrCore.ReadEntityPresenceAsync), MonitorHost.Json(200, "{}"));
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

    [Fact]
    public async Task AnUnnarrowedRunReachesEveryPage()
    {
        await using var host = await HostOverAsync(PagedCatalogue());
        host.Client.Answering(nameof(RecordingWhisparrCore.AddSceneAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrCore.ReadSceneByRemoteIdAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrCore.ReadEntityPresenceAsync), MonitorHost.Json(200, "{}"));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        await MonitorAllAsync(host, "studio", studioId);
        var offered = await RunAndReadOfferedAsync(host);

        Assert.Equal(Catalogue, offered.Count);
        Assert.Equal(Catalogue, offered.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task TheRunIssuesTheSceneAddAndNoOtherVerb()
    {
        await using var host = await HostOverAsync(PagedCatalogue());
        host.Client.Answering(nameof(RecordingWhisparrCore.AddSceneAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrCore.ReadSceneByRemoteIdAsync), MonitorHost.Json(200, "{}"));
        host.Client.Answering(nameof(RecordingWhisparrCore.ReadEntityPresenceAsync), MonitorHost.Json(200, "{}"));
        var studioId = await host.SeedStudioAsync(
            MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

        await MonitorAllAsync(host, "studio", studioId);
        host.Client.Verbs.Clear();
        await host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        // The catalogue read is the run's own source of scenes, and the two defaults reads compose
        // the add. Nothing else reaches the instance, and in particular no search.
        Assert.Equal(
            [
                nameof(IWhisparrMissingSceneActing.AddSceneAsync),
                nameof(IWhisparrEntityCatalogueReading.ReadEntityCatalogueAsync),
                nameof(IWhisparrClient.ReadQualityProfilesAsync),
                nameof(IWhisparrClient.ReadRootFoldersAsync),
            ],
            host.Client.Verbs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
    }

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

    [Fact]
    public void AnUnnarrowedRunCarriesNoNarrowing()
    {
        var decoded = MissingMonitorAllJob.Decode(
            MissingMonitorAllJob.Encode(WhisparrEntityKind.Studio, 7, null, "  "));

        Assert.Null(decoded.TitleSearch);
        Assert.Null(decoded.Filters);
    }
}
