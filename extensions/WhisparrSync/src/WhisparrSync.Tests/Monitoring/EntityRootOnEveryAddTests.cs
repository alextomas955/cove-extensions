using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// The root each add doorway composes with, driven through the shipped routes in process.
/// </summary>
/// <remarks>
/// Every case declares two library roots and two instance roots, and the instance's first declared
/// root is never the one the entity's files sit under. A doorway that fell back to the instance's own
/// first answer is therefore visible rather than coincidentally right.
/// <para>
/// Asserted on the body the instance received, not on the values a seam was handed: the add body is
/// composed below the level a call site can see.
/// </para>
/// </remarks>
public sealed class EntityRootOnEveryAddTests
{
    /// <summary>The library root the instance lists first, holding none of the entity's files.</summary>
    private const string FirstCoveRoot = "G:/Downloads/P";

    /// <summary>The library root the entity's own files sit under.</summary>
    private const string SecondCoveRoot = "I:/Downloads/P";

    private const string FirstInstanceRoot = "/g-downloads-p/videos";

    private const string SecondInstanceRoot = "/i-downloads-p/videos";

    private const string Folder = SecondCoveRoot + "/Exploited College Girls";

    /// <summary>The profile the instance offers first, which is not the lowest numbered.</summary>
    private const int OfferedProfileId = 4;

    private const long SampleSize = 41;

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";
    private const string ThirdScene = "b4d1e7c0-5a62-4f19-9d3e-0c8a7f26b514";

    /// <summary>The entity read answering that the instance already holds the studio.</summary>
    private const string HeldStudio =
        """{"id":9,"foreignId":"44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e","monitored":true}""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// Monitoring one studio from the entity menu adds it at the root its own files sit under.
    /// </summary>
    [Fact]
    public async Task TheMonitorAddCarriesTheRootTheStudiosOwnFilesSitUnder()
    {
        await using var fixture = await StudioFixture.CreateAsync();

        await fixture.Host.MonitorAsync("studio", fixture.StudioId);

        Assert.Equal(SecondInstanceRoot, RootOn(fixture.SingleAdd(WhisparrClient.StudioPath)));
    }

    /// <summary>
    /// That studio, with the instance agreeing no spelling for its root, has no add sent for it.
    /// </summary>
    /// <remarks>
    /// The reason names this entity's own library root rather than the instance's root list. The two
    /// send a reader to different places: the instance's list is perfectly good here, and what is
    /// unsettled is which of those roots holds this studio's files.
    /// </remarks>
    [Fact]
    public async Task AStudioWhoseRootAgreedOnNothingIsNotAdded()
    {
        await using var fixture = await StudioFixture.CreateAsync(instanceHoldsTheSample: false);

        var view = await fixture.Host.MonitorAsync("studio", fixture.StudioId);

        Assert.Equal(MonitorRefusalKind.NoAgreedRootForThisEntity, view.Refusal);
        Assert.DoesNotContain(fixture.Sent, call => call.Method == HttpMethod.Post);
    }

    /// <summary>
    /// A studio the library holds no file for is still added, at the root the instance offered first.
    /// </summary>
    /// <remarks>
    /// It has nothing to derive a root from, which is a different fact from a root the instance would
    /// not agree to. Refusing it would stop adds that work today for a defect they do not have.
    /// </remarks>
    [Fact]
    public async Task AStudioOwningNoFileIsAddedAtTheRootTheInstanceOfferedFirst()
    {
        await using var fixture = await StudioFixture.CreateAsync(seedTheFile: false);

        await fixture.Host.MonitorAsync("studio", fixture.StudioId);

        Assert.Equal(FirstInstanceRoot, RootOn(fixture.SingleAdd(WhisparrClient.StudioPath)));
    }

    /// <summary>Adding every missing scene for that studio composes with the studio's own root.</summary>
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

    /// <summary>A selection of missing scenes composes with the studio's own root.</summary>
    [Fact]
    public async Task ASelectionOfMissingScenesComposesWithTheStudiosRoot()
    {
        await using var fixture = await StudioFixture.CreateAsync();

        await fixture.MarkSelectedAsync(FirstScene, SecondScene, ThirdScene);

        Assert.All(
            fixture.Adds(MoviePath),
            body => Assert.Equal(SecondInstanceRoot, RootOn(body)));
    }

    /// <summary>
    /// The selection composes its root once for the run rather than once for each scene in it.
    /// </summary>
    /// <remarks>
    /// Counted on the library reads the composition takes, one per configured root. The agreement
    /// behind it is cached per root, so a per-scene composition would still repeat the counts for
    /// every scene on a page.
    /// </remarks>
    [Fact]
    public async Task ASelectionComposesItsRootOnceForTheRun()
    {
        await using var fixture = await StudioFixture.CreateAsync();

        await fixture.MarkSelectedAsync(FirstScene, SecondScene, ThirdScene);

        Assert.Equal(3, fixture.Adds(MoviePath).Count);
        Assert.Equal([FirstCoveRoot, SecondCoveRoot], fixture.Host.RootCounts);
    }

    /// <summary>Adding one missing scene from a card composes with the route entity's root.</summary>
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

    /// <summary>
    /// Adding one scene composes with the root that scene's own Cove file sits under.
    /// </summary>
    /// <remarks>
    /// The studio's other files sit under the first root, so a composition keyed on the owning
    /// entity rather than on the video would send this scene to the wrong one.
    /// </remarks>
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

    /// <summary>
    /// A scene the library holds no file for is still added, at the root the instance offered first.
    /// </summary>
    [Fact]
    public async Task ASceneOwningNoFileIsAddedAtTheRootTheInstanceOfferedFirst()
    {
        await using var fixture = await StudioFixture.CreateAsync();
        var videoId = await fixture.Host.SeedStudioSceneAsync(
            fixture.StudioId, MonitorHost.StoredEndpoint, FirstScene);

        await fixture.Host.SceneActionAsync(videoId, "add");

        Assert.Equal(FirstInstanceRoot, RootOn(fixture.SingleAdd(MoviePath)));
    }

    /// <summary>
    /// A scene whose root agreed on nothing names its own library folder as the cause, not the
    /// instance.
    /// </summary>
    /// <remarks>
    /// Nothing was sent, so a reason saying the instance declined would name the wrong party and
    /// send a reader to an instance whose own settings are perfectly good.
    /// </remarks>
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

    /// <summary>The card's own add names it the same way.</summary>
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

    /// <summary>The path a v3 instance takes a scene add on.</summary>
    private const string MoviePath = "api/v3/movie";

    /// <summary>The root member of one serialized add body.</summary>
    private static string RootOn(string body)
        => Assert.IsType<JsonObject>(JsonNode.Parse(body))["rootFolderPath"]!.GetValue<string>();

    /// <summary>
    /// One host over one studio holding one file under the second of two library roots.
    /// </summary>
    /// <remarks>
    /// The listing the folder probe reads is composed from the file the library really seeded, so the
    /// instance is asked about the path the product really asked about rather than one this case
    /// guessed at.
    /// </remarks>
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

        /// <summary>States that the instance holds <paramref name="covePath"/> under its own root.</summary>
        /// <remarks>
        /// The probe's listing is composed from a path the library really seeded, so the instance is
        /// asked about the path the product really asked about rather than one this case guessed at.
        /// </remarks>
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

                // The entity read's STATUS is what says whether the instance holds the studio, so a
                // read answering a success would describe an instance holding every entity asked about
                // and the monitor path would never compose an add at all.
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

        /// <summary>Marks <paramref name="providerSceneIds"/> wanted, and runs the enqueued pass.</summary>
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

        /// <summary>The bodies posted to <paramref name="path"/>, in order.</summary>
        public IReadOnlyList<string> Adds(string path)
            => [.. _bytes.Requests
                .Where(sent => sent.Method == HttpMethod.Post
                    && sent.Path.EndsWith(path, StringComparison.Ordinal))
                .Select(sent => sent.Body)];

        /// <summary>The one body posted to <paramref name="path"/>.</summary>
        public string SingleAdd(string path) => Assert.Single(Adds(path));

        /// <summary>One folder listing holding <paramref name="instancePath"/> and nothing else.</summary>
        private static string ListingHolding(string instancePath)
            => $$"""
                {"parent":"{{SecondInstanceRoot}}/","directories":[],
                 "files":[{"path":"{{instancePath}}","size":{{SampleSize}}}]}
                """;

        public ValueTask DisposeAsync() => Host.DisposeAsync();
    }
}
