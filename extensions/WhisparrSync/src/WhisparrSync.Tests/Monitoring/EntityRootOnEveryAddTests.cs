using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// Every case declares two library roots and two instance roots, and the instance's first declared
// root is never the one the entity's files sit under, so a fallback to the instance's first answer
// shows up rather than passing by coincidence. Assertions read the body the instance received,
// because the add body is composed below the level a call site can see.
public sealed class EntityRootOnEveryAddTests
{
    // The library root the instance lists first, holding none of the entity's files.
    private const string FirstCoveRoot = "G:/Downloads/P";

    // The library root the entity's own files sit under.
    private const string SecondCoveRoot = "I:/Downloads/P";

    private const string FirstInstanceRoot = "/g-downloads-p/videos";

    private const string SecondInstanceRoot = "/i-downloads-p/videos";

    private const string Folder = SecondCoveRoot + "/Exploited College Girls";

    // The profile the instance offers first, which is not the lowest numbered.
    private const int OfferedProfileId = 4;

    private const long SampleSize = 41;

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";
    private const string ThirdScene = "b4d1e7c0-5a62-4f19-9d3e-0c8a7f26b514";

    // The entity read answering that the instance already holds the studio.
    private const string HeldStudio =
        """{"id":9,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true}""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheMonitorAddCarriesTheRootTheStudiosOwnFilesSitUnder()
    {
        await using var fixture = await StudioFixture.CreateAsync();

        await fixture.Host.MonitorAsync("studio", fixture.StudioId);

        Assert.Equal(SecondInstanceRoot, RootOn(fixture.SingleAdd(WhisparrV3Instance.StudioPath)));
    }

    // The refusal names this entity's own library root, not the instance's root list. The
    // instance's list is fine here; what is unsettled is which root holds this studio's files.
    [Fact]
    public async Task AStudioWhoseRootAgreedOnNothingIsNotAdded()
    {
        await using var fixture = await StudioFixture.CreateAsync(instanceHoldsTheSample: false);

        var view = await fixture.Host.MonitorAsync("studio", fixture.StudioId);

        Assert.Equal(MonitorRefusalKind.NoAgreedRootForThisEntity, view.Refusal);
        Assert.DoesNotContain(fixture.Sent, call => call.Method == HttpMethod.Post);
    }

    // The refusal names the settings page as the remedy, and that page lists a root only once
    // something has recorded a reading for it. Without this the reader reaches a page that does not
    // list the folder.
    [Fact]
    public async Task AStudioWhoseRootAgreedOnNothingPutsThatRootOnTheSettingsPage()
    {
        await using var fixture = await StudioFixture.CreateAsync(instanceHoldsTheSample: false);

        await fixture.Host.MonitorAsync("studio", fixture.StudioId);

        var listed = Assert.Single(
            (await fixture.Host.ReadFolderMappingsAsync()).Roots,
            root => root.Root == SecondCoveRoot);
        Assert.NotNull(listed.Refusal);
    }

    // A studio with no file has nothing to derive a root from, which is a different fact from a
    // root the instance would not agree to, so it is added rather than refused.
    [Fact]
    public async Task AStudioOwningNoFileIsAddedAtTheRootTheInstanceOfferedFirst()
    {
        await using var fixture = await StudioFixture.CreateAsync(seedTheFile: false);

        await fixture.Host.MonitorAsync("studio", fixture.StudioId);

        Assert.Equal(FirstInstanceRoot, RootOn(fixture.SingleAdd(WhisparrV3Instance.StudioPath)));
    }

    [Fact]
    public async Task AddingEveryMissingSceneComposesWithTheStudiosRoot()
    {
        await using var fixture = await StudioFixture.CreateAsync(studioRead: HeldStudio);
        await fixture.Host.SeedStudioSceneAsync(
            fixture.StudioId, MonitorHost.StoredEndpoint, FirstScene);

        await fixture.Host.AddAllMissingViewAsync("studio", fixture.StudioId);
        await fixture.Host.RunEnqueuedBatchAsync(new RecordingJobProgress());

        Assert.Equal(SecondInstanceRoot, RootOn(fixture.SingleAdd(MoviePath)));
    }

    [Fact]
    public async Task ASelectionOfMissingScenesComposesWithTheStudiosRoot()
    {
        await using var fixture = await StudioFixture.CreateAsync();

        await fixture.MarkSelectedAsync(FirstScene, SecondScene, ThirdScene);

        Assert.All(
            fixture.Adds(MoviePath),
            body => Assert.Equal(SecondInstanceRoot, RootOn(body)));
    }

    // Counted on the library reads the composition takes, one per configured root. A per-scene
    // composition would repeat those reads for every scene on a page.
    [Fact]
    public async Task ASelectionComposesItsRootOnceForTheRun()
    {
        await using var fixture = await StudioFixture.CreateAsync();

        await fixture.MarkSelectedAsync(FirstScene, SecondScene, ThirdScene);

        Assert.Equal(3, fixture.Adds(MoviePath).Count);
        Assert.Equal([FirstCoveRoot, SecondCoveRoot], fixture.Host.RootCounts);
    }

    [Fact]
    public async Task OneMissingSceneFromACardComposesWithTheRouteEntitysRoot()
    {
        await using var fixture = await StudioFixture.CreateAsync();

        var answered = await fixture.Host.Http.PostAsync(
            fixture.Host.RouteFor("studio", fixture.StudioId, "missing/" + FirstScene + "/monitor"),
            content: null,
            TestCt);
        answered.EnsureSuccessStatusCode();

        Assert.Equal(SecondInstanceRoot, RootOn(fixture.SingleAdd(MoviePath)));
    }

    // The studio's other files sit under the first root, so a composition keyed on the owning
    // entity rather than on the video would send this scene to the wrong root.
    [Fact]
    public async Task OneSceneIsAddedAtTheRootItsOwnFileSitsUnder()
    {
        await using var fixture = await StudioFixture.CreateAsync(seedTheFile: false);
        await fixture.Host.SeedStudioFileAsync(
            fixture.StudioId, FirstCoveRoot + "/Exploited College Girls", SampleSize);
        var videoId = await fixture.Host.SeedStudioSceneAsync(
            fixture.StudioId, MonitorHost.StoredEndpoint, FirstScene);
        fixture.InstanceHolds(await fixture.Host.SeedSceneFileAsync(videoId, Folder));

        await fixture.Host.SceneActionAsync(videoId, "add");

        Assert.Equal(SecondInstanceRoot, RootOn(fixture.SingleAdd(MoviePath)));
    }

    [Fact]
    public async Task ASceneOwningNoFileIsAddedAtTheRootTheInstanceOfferedFirst()
    {
        await using var fixture = await StudioFixture.CreateAsync();
        var videoId = await fixture.Host.SeedStudioSceneAsync(
            fixture.StudioId, MonitorHost.StoredEndpoint, FirstScene);

        await fixture.Host.SceneActionAsync(videoId, "add");

        Assert.Equal(FirstInstanceRoot, RootOn(fixture.SingleAdd(MoviePath)));
    }

    // Nothing was sent, so a refusal saying the instance declined would name the wrong party.
    [Fact]
    public async Task ASceneWhoseRootAgreedOnNothingNamesItsOwnFolderAsTheCause()
    {
        await using var fixture = await StudioFixture.CreateAsync(instanceHoldsTheSample: false);
        var videoId = await fixture.Host.SeedStudioSceneAsync(
            fixture.StudioId, MonitorHost.StoredEndpoint, FirstScene);
        await fixture.Host.SeedSceneFileAsync(videoId, Folder);

        var answered = await fixture.Host.SceneActionAsync(videoId, "add");

        Assert.Equal(SceneRefusalKind.NoAgreedRootForThisEntity, answered.Refusal);
        Assert.DoesNotContain(fixture.Sent, call => call.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task AMissingSceneCardWhoseRootAgreedOnNothingNamesItsOwnFolderAsTheCause()
    {
        await using var fixture = await StudioFixture.CreateAsync(instanceHoldsTheSample: false);

        var answered = await fixture.Host.Http.PostAsync(
            fixture.Host.RouteFor("studio", fixture.StudioId, "missing/" + FirstScene + "/monitor"),
            content: null,
            TestCt);
        answered.EnsureSuccessStatusCode();
        var result = (await answered.Content.ReadFromJsonAsync<MissingSceneActionResult>(TestCt))!;

        Assert.Equal(MissingSceneActionRefusal.NoAgreedRootForThisEntity, result.Refusal);
        Assert.DoesNotContain(fixture.Sent, call => call.Method == HttpMethod.Post);
    }

    // The path a v3 instance takes a scene add on.
    private const string MoviePath = "api/v3/movie";

    private static string RootOn(string body)
        => Assert.IsType<JsonObject>(JsonNode.Parse(body))["rootFolderPath"]!.GetValue<string>();

    private sealed class StudioFixture : IAsyncDisposable
    {
        private readonly BodyRecordingHandler _bytes;
        private readonly string[] _listing;

        private StudioFixture(
            MonitorHost host, BodyRecordingHandler bytes, string[] listing, int studioId)
        {
            Host = host;
            _bytes = bytes;
            _listing = listing;
            StudioId = studioId;
        }

        // The probe's listing is composed from a path the library really seeded, so the instance is
        // asked about the path the product asked about rather than one this case guessed at.
        public void InstanceHolds(string covePath)
            => _listing[0] = ListingHolding(
                covePath.Replace(SecondCoveRoot, SecondInstanceRoot, StringComparison.Ordinal));

        public MonitorHost Host { get; }

        public int StudioId { get; }

        public IReadOnlyList<(HttpMethod Method, string Path, string Body)> Sent => _bytes.Requests;

        public static async Task<StudioFixture> CreateAsync(
            bool seedTheFile = true,
            bool instanceHoldsTheSample = true,
            string? studioRead = null)
        {
            var listing = new[]
            {
                $$"""{"parent":"{{SecondInstanceRoot}}/","directories":[],"files":[]}""",
            };

            var bytes = BodyRecordingHandler.AnsweringEach((method, path) =>
            {
                if (path.EndsWith("/qualityprofile", StringComparison.Ordinal))
                {
                    return (HttpStatusCode.OK,
                        $$"""[{"id":{{OfferedProfileId}},"name":"Any"},{"id":1,"name":"HD-1080p"}]""");
                }

                if (path.EndsWith("/rootfolder", StringComparison.Ordinal))
                {
                    return (HttpStatusCode.OK, $$"""
                        [{"id":1,"path":"{{FirstInstanceRoot}}","accessible":true},
                         {"id":2,"path":"{{SecondInstanceRoot}}","accessible":true}]
                        """);
                }

                if (path.EndsWith("/filesystem", StringComparison.Ordinal))
                {
                    return (HttpStatusCode.OK, listing[0]);
                }

                // The entity read's status is what says whether the instance holds the studio, so a
                // read answering a success would describe an instance holding every entity asked
                // about and the monitor path would never compose an add.
                if (method == HttpMethod.Get
                    && path.Contains("/studio/", StringComparison.Ordinal))
                {
                    return studioRead is null
                        ? (HttpStatusCode.NotFound, string.Empty)
                        : (HttpStatusCode.OK, studioRead);
                }

                return (HttpStatusCode.OK, "[]");
            });

            var host = await MonitorHost.CreateAsync(
                bytes: bytes,
                libraryConfig: new CoveConfiguration
                {
                    CovePaths =
                    [
                        new CovePath { Path = FirstCoveRoot },
                        new CovePath { Path = SecondCoveRoot },
                    ],
                });

            var studioId = await host.SeedStudioAsync(
                MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

            var fixture = new StudioFixture(host, bytes, listing, studioId);

            if (seedTheFile)
            {
                var seeded = await host.SeedStudioFileAsync(studioId, Folder, SampleSize);
                if (instanceHoldsTheSample)
                {
                    fixture.InstanceHolds(seeded);
                }
            }

            return fixture;
        }

        public async Task MarkSelectedAsync(params string[] providerSceneIds)
        {
            var ticked = string.Join(",", providerSceneIds.Select(id => $"\"{id}\""));
            using var content = new StringContent(
                $$"""{"providerSceneIds":[{{ticked}}]}""", Encoding.UTF8, "application/json");
            var enqueued = await Host.Http.PostAsync(
                Host.RouteFor("studio", StudioId, "missing/bulk-monitor"), content, TestCt);
            enqueued.EnsureSuccessStatusCode();

            await Host.RunEnqueuedBatchAsync(new RecordingJobProgress());
        }

        public IReadOnlyList<string> Adds(string path)
            => [.. _bytes.Requests
                .Where(sent => sent.Method == HttpMethod.Post
                    && sent.Path.EndsWith(path, StringComparison.Ordinal))
                .Select(sent => sent.Body)];

        public string SingleAdd(string path) => Assert.Single(Adds(path));

        private static string ListingHolding(string instancePath)
            => $$"""
                {"parent":"{{SecondInstanceRoot}}/","directories":[],
                 "files":[{"path":"{{instancePath}}","size":{{SampleSize}}}]}
                """;

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
