using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Cove.Core.Interfaces;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// Every case declares two library roots and two instance roots, and the instance's first declared
// root is never the one the studio's files sit under, so a composition falling back to the
// instance's own first answer is visible rather than coincidentally right. Asserted on the body the
// instance received, not on the values a seam was handed: the add body is composed below the level
// a call site can see.
public sealed class SiteRootRegistrationTests
{
    // The namespace v2 identifies a site in.
    private const string V2Endpoint = "https://theporndb.net/graphql";

    private const string SiteRemoteId = "a30bc641-6afe-4c80-9c73-ecb68104a68d";

    private const int SiteNumber = 3372;

    // The library root the instance lists first, holding none of the studio's files.
    private const string FirstCoveRoot = "G:/Downloads/P";

    private const string SecondCoveRoot = "I:/Downloads/P";

    private const string FirstInstanceRoot = "/g-downloads-p/videos";

    private const string SecondInstanceRoot = "/i-downloads-p/videos";

    private const string Folder = SecondCoveRoot + "/Exploited College Girls";

    // The profile the instance offers first, which is not the lowest numbered.
    private const int OfferedProfileId = 4;

    private const long SampleSize = 41;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheAddCarriesTheRootTheStudiosOwnFilesSitUnder()
    {
        var (host, _) = await RunAsync();
        await using var driven = host;

        var body = Assert.IsType<JsonObject>(JsonNode.Parse(SingleAdd(host).Body));

        Assert.Equal(SecondInstanceRoot, body["rootFolderPath"]!.GetValue<string>());
        Assert.NotEqual(FirstInstanceRoot, body["rootFolderPath"]!.GetValue<string>());
    }

    // The root is a property of the studio and the profile is a property of the run, so choosing the
    // root per studio must not drag the profile along with it.
    [Fact]
    public async Task TheProfileBesideItIsStillTheOneTheInstanceOfferedFirst()
    {
        var (host, _) = await RunAsync();
        await using var driven = host;

        var body = Assert.IsType<JsonObject>(JsonNode.Parse(SingleAdd(host).Body));

        Assert.Equal(OfferedProfileId, body["qualityProfileId"]!.GetValue<int>());
    }

    // The instance answers an empty directory at every candidate, which is what a real container
    // answers for a path it has no counterpart for. Registering anyway would put the site at a root
    // holding none of its files.
    [Fact]
    public async Task AStudioWhoseRootAgreedOnNothingHasNoAddSentForIt()
    {
        var (host, progress) = await RunAsync(instanceHoldsTheSample: false);
        await using var driven = host;

        Assert.DoesNotContain(
            host.Bytes!.Requests,
            sent => sent.Method == HttpMethod.Post
                && sent.Path.EndsWith("/series", StringComparison.Ordinal));
        Assert.Contains(
            "0 sites registered", Assert.Single(progress.Summaries), StringComparison.Ordinal);
    }

    // The read is what decides whether the site is already there, and a site already there has its
    // scenes marked whatever root it sits at. A probe that cannot be answered refuses the same way
    // for every site in the run, so stopping before the read would leave a whole run's scenes
    // unflagged over a folder mapping.
    [Fact]
    public async Task AStudioWhoseRootAgreedOnNothingIsStillReadOnTheInstance()
    {
        var (host, progress) = await RunAsync(instanceHoldsTheSample: false);
        await using var driven = host;

        Assert.Contains(
            host.Bytes!.Requests,
            sent => sent.Method == HttpMethod.Get
                && sent.Path.EndsWith("/series/lookup", StringComparison.Ordinal));
        Assert.Contains(
            "1 with no agreed root",
            Assert.Single(progress.Summaries),
            StringComparison.Ordinal);
    }

    // The two reasons send a reader to different places. The instance's root list is good here, so a
    // reader sent to check it would find nothing wrong; what is unsettled is which of those roots
    // holds this studio's files.
    [Fact]
    public async Task TheReasonNamesThisEntitysOwnRootRatherThanTheInstancesList()
    {
        var composed = await EntityAddDefaults.ComposeAsync(
            new AddDefaults(OfferedProfileId, FirstInstanceRoot),
            [FirstCoveRoot, SecondCoveRoot],
            (coveRoot, _) => Task.FromResult(coveRoot == SecondCoveRoot ? 1562 : 0),
            (coveRoot, _) => Task.FromResult(
                new AddressedFolder(
                    null, FolderAgreementRefusal.NothingResolved, coveRoot, [])),
            TestCt);

        Assert.Null(composed.Defaults);
        Assert.Equal(MonitorRefusalKind.NoAgreedRootForThisEntity, composed.Refusal);
        Assert.Equal(SecondCoveRoot, composed.Root.CoveRoot);
    }

    private static (HttpMethod Method, string Path, string Body) SingleAdd(MonitorHost host)
        => Assert.Single(
            host.Bytes!.Requests,
            sent => sent.Method == HttpMethod.Post
                && sent.Path.EndsWith("/series", StringComparison.Ordinal));

    // The listing the probe reads is composed from the file the library seeded, so the instance is
    // asked about the path the product asked about rather than one this fixture guessed.
    private static async Task<(MonitorHost Host, RecordingJobProgress Progress)> RunAsync(
        bool instanceHoldsTheSample = true)
    {
        var empty = $$"""{"parent":"{{SecondInstanceRoot}}/","directories":[],"files":[]}""";
        var listing = new[] { empty };

        var bytes = BodyRecordingHandler.AnsweringByPath(path => path switch
        {
            var route when route.EndsWith("/qualityprofile", StringComparison.Ordinal)
                => $$"""[{"id":{{OfferedProfileId}},"name":"Any"},{"id":1,"name":"HD-1080p"}]""",
            var route when route.EndsWith("/rootfolder", StringComparison.Ordinal)
                => $$"""
                    [{"id":1,"path":"{{FirstInstanceRoot}}","accessible":true},
                     {"id":2,"path":"{{SecondInstanceRoot}}","accessible":true}]
                    """,
            var route when route.EndsWith("/filesystem", StringComparison.Ordinal) => listing[0],

            // The site read, answering a row the instance holds nothing under, so the add that
            // follows is the subject of these cases rather than a read that refused ahead of it.
            var route when route.EndsWith("/series/lookup", StringComparison.Ordinal)
                => $$"""[{"tvdbId":{{SiteNumber}},"title":"Exploited College Girls"}]""",
            _ => "[]",
        });

        var host = await MonitorHost.CreateAsync(
            generation: WhisparrGeneration.V2,
            bytes: bytes,
            siteNumbers: TestSiteNumbers.Numbering(SiteRemoteId, SiteNumber),
            libraryConfig: new CoveConfiguration
            {
                CovePaths =
                [
                    new CovePath { Path = FirstCoveRoot },
                    new CovePath { Path = SecondCoveRoot },
                ],
            });

        var studioId = await host.SeedStudioAsync(V2Endpoint, SiteRemoteId);
        var seeded = await host.SeedStudioFileAsync(studioId, Folder, SampleSize);

        if (instanceHoldsTheSample)
        {
            var onInstance = seeded.Replace(
                SecondCoveRoot, SecondInstanceRoot, StringComparison.Ordinal);
            listing[0] = $$"""
                {"parent":"{{SecondInstanceRoot}}/","directories":[],
                 "files":[{"path":"{{onInstance}}","size":{{SampleSize}}}]}
                """;
        }

        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var enqueued = await host.Http.PostAsync(
            "/api/extensions/" + host.ExtensionId + "/sync/run", content, TestCt);
        enqueued.EnsureSuccessStatusCode();
        Assert.Equal(
            SyncRefusalKind.None,
            (await enqueued.Content.ReadFromJsonAsync<SyncEnqueued>(TestCt))!.Refusal);

        var progress = new RecordingJobProgress();
        await host.RunEnqueuedBatchAsync(progress);
        return (host, progress);
    }
}
