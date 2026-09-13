using System.Net.Http.Json;
using System.Text;
using Cove.Core.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Jobs;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;
using WhisparrSync.Scene;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Jobs;

/// <summary>
/// The site pass, where presence is a site rather than a scene: what the run registers, what a
/// re-run does, how many sites the count reads, and what a read that could not be answered leaves.
/// </summary>
/// <remarks>
/// Driven through the mounted route and the recording seam rather than by reading source. The
/// recording client refuses a verb it was not given an answer for, so a request this pass must not
/// make faults the run instead of passing unnoticed.
/// </remarks>
public sealed class SyncLibrarySitesTests
{
    /// <summary>The namespace v2 identifies a site in.</summary>
    private const string V2Endpoint = "https://theporndb.net/graphql";

    private const string FirstSite = "a30bc641-6afe-4c80-9c73-ecb68104a68d";

    private const string SecondSite = "44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";

    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    /// <summary>The number the metadata provider issues for each scene, as this generation names it.</summary>
    private static readonly Dictionary<string, int> SceneNumbers = new(StringComparer.Ordinal)
    {
        [FirstScene] = 1363738,
        [SecondScene] = 1363739,
    };

    /// <summary>
    /// The instance's own row identifier for each scene, which is not the number above.
    /// </summary>
    /// <remarks>
    /// Held apart on purpose: a pass that set the flag by the provider's number rather than by the
    /// row the instance answered would pass against one shared value.
    /// </remarks>
    private static readonly Dictionary<string, int> SceneRows = new(StringComparer.Ordinal)
    {
        [FirstScene] = 77,
        [SecondScene] = 88,
    };

    /// <summary>A provider holding an answer for nothing at all, so any resolution faults the run.</summary>
    private static readonly Dictionary<string, int?> NoAnswers = new(StringComparer.Ordinal);

    /// <summary>The instance's own numeric id for a site it took, as its add's answer names it.</summary>
    private const int RegisteredSiteId = 11;

    /// <summary>The instance's own numeric id for a site it already held.</summary>
    private const int HeldSiteId = 9;

    private static readonly string RegisteredRow =
        $$"""{"id":{{RegisteredSiteId}},"title":"Jay Bank Presents"}""";

    private static readonly string HeldRow =
        $$"""{"id":{{HeldSiteId}},"title":"Jay Bank Presents"}""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// Every site the library names is registered once, and with the monitor toggle off no monitor
    /// call is made at all.
    /// </summary>
    /// <remarks>
    /// Scoped to the toggle being off on purpose. With it on, what the reader owns on a site is its
    /// scenes, and marking those is a separate capability this pass does not obtain - so a case
    /// asserting that no monitor call is ever made would be a case that has to be deleted once it is.
    /// <para>
    /// The absence is proved by the recording client refusing a verb it was given no answer for
    /// rather than by a zero count: a zero count also passes over a run that walked nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WithTheToggleOffTheSitePassRegistersEachSiteAndMakesNoMonitorCall()
    {
        await using var host = await SiteHost(held: false);
        await host.SeedStudioAsync(V2Endpoint, FirstSite);
        await host.SeedStudioAsync(V2Endpoint, SecondSite);

        var progress = await RunAsync(host);

        Assert.Equal(
            [FirstSite, SecondSite],
            Verb(host, nameof(IWhisparrSiteRegistrationActing.RegisterSiteAsync))
                .Select(call => call.ForeignId));
        Assert.Empty(host.Client.UnexpectedCalls);
        Assert.All(
            host.Client.Verbs,
            verb => Assert.DoesNotContain("Monitor", verb, StringComparison.Ordinal));
        Assert.Equal([2], progress.DeclaredUnitCounts);
        Assert.Contains("2 sites registered", Assert.Single(progress.Summaries), StringComparison.Ordinal);
    }

    /// <summary>
    /// A site the instance already holds is counted as already held, and no add is composed for it.
    /// </summary>
    /// <remarks>
    /// Which is what makes a second pass over the same library create no duplicate. The recording
    /// client is given no answer for the add at all, so composing one faults the run.
    /// </remarks>
    [Fact]
    public async Task ASiteTheInstanceAlreadyHoldsIsCountedAsAlreadyHeldAndGetsNoSecondAdd()
    {
        await using var host = await SiteHost(held: true);
        await host.SeedStudioAsync(V2Endpoint, FirstSite);
        await host.SeedStudioAsync(V2Endpoint, SecondSite);

        var progress = await RunAsync(host);

        Assert.DoesNotContain(
            nameof(IWhisparrSiteRegistrationActing.RegisterSiteAsync), host.Client.Verbs);
        Assert.Empty(host.Client.UnexpectedCalls);
        Assert.All(progress.Units, unit => Assert.Equal(JobUnitOutcome.Skipped, unit.Outcome));
        Assert.Contains(
            "0 sites registered, 2 already in Whisparr",
            Assert.Single(progress.Summaries),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The per-site outcome carries the instance's own id, off whichever answer the step already
    /// read.
    /// </summary>
    /// <remarks>
    /// The add's own answer where the site was added, the held row's where it was already there.
    /// Nothing reads the site a second time to learn an id the instance has just stated.
    /// </remarks>
    [Fact]
    public async Task ThePerSiteOutcomeCarriesTheInstancesOwnIdWithNoSecondRead()
    {
        var reads = new List<string>();
        var adds = new List<string>();

        var registered = await SiteRegistrationStep.RegisterAsync(
            Answering(reads, RecordingWhisparrClient.Json(404, string.Empty)),
            Answering(adds, RecordingWhisparrClient.Json(201, RegisteredRow)),
            new LibrarySiteIdentity(4, FirstSite),
            TestCt);

        var alreadyThere = await SiteRegistrationStep.RegisterAsync(
            Answering(reads, RecordingWhisparrClient.Json(200, HeldRow)),
            Answering(adds, RecordingWhisparrClient.Json(201, RegisteredRow)),
            new LibrarySiteIdentity(7, SecondSite),
            TestCt);

        Assert.Equal(SceneRegistration.Registered, registered.Registration);
        Assert.Equal(RegisteredSiteId, registered.InstanceId);
        Assert.Equal(SceneRegistration.AlreadyHeld, alreadyThere.Registration);
        Assert.Equal(HeldSiteId, alreadyThere.InstanceId);

        Assert.Equal([FirstSite, SecondSite], reads);
        Assert.Equal([FirstSite], adds);
    }

    /// <summary>A read that answered neither presence nor absence registers nothing.</summary>
    /// <remarks>
    /// Registering on an answer nothing could be read out of would add a site the instance may
    /// already hold, and this generation publishes no contract for what that answer then is.
    /// </remarks>
    [Fact]
    public async Task ASiteWhoseReadAnsweredNeitherIsRefusedRatherThanRegistered()
    {
        var adds = new List<string>();

        var outcome = await SiteRegistrationStep.RegisterAsync(
            (_, _) => Task.FromResult<WhisparrResponse?>(null),
            Answering(adds, RecordingWhisparrClient.Json(201, RegisteredRow)),
            new LibrarySiteIdentity(4, FirstSite),
            TestCt);

        Assert.Equal(SceneRegistration.Refused, outcome.Registration);
        Assert.Null(outcome.InstanceId);
        Assert.Empty(adds);
    }

    /// <summary>
    /// The site count reads every site in the stream and truncates nothing, at any number of sites.
    /// </summary>
    /// <remarks>
    /// Seeded past <see cref="SyncPreviewJob.ChunkSize"/>, which is the largest batching figure
    /// declared anywhere on this path and has nothing to do with the pacing bound. Sized against the
    /// pacing bound, which is one, this would go green again the moment someone capped the pass at a
    /// hundred; sized against the chunk it reddens on a ceiling introduced at any value below the
    /// seed.
    /// <para>
    /// This stands in for the failure nobody can observe at three studios: a ceiling would answer a
    /// short already-there and not-yet-there pair that reads exactly like a complete one, which is
    /// why the pair is asserted to add back up to the number seeded.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSiteCountReadsEverySiteInTheStreamAndTruncatesNothing()
    {
        var seeded = Sites(SyncPreviewJob.ChunkSize + 1);
        var asked = new List<string>();

        var counted = await CountAsync(
            seeded,
            (identity, _) =>
            {
                asked.Add(identity);
                return Task.FromResult(asked.Count % 2 == 0);
            });

        Assert.NotNull(counted);
        Assert.Equal(seeded.Count, asked.Count);
        Assert.Equal(seeded.Count, counted.AlreadyThere + counted.NotYetThere);
        Assert.Equal(SyncRegisters.Sites, counted.Registers);
    }

    /// <summary>The pacing bound is a bound on reads in flight, and it is one.</summary>
    /// <remarks>
    /// Read from the constant rather than restated, and asserted beside the case above: the two
    /// together say that what is bounded is how many reads are outstanding and not how many are
    /// issued.
    /// </remarks>
    [Fact]
    public void ThePacingBoundIsOneReadInFlightAndBoundsNoTotal()
    {
        Assert.Equal(1, SyncPreviewJob.SitePresenceReadsInFlight);
        Assert.True(SyncPreviewJob.SitePresenceReadsInFlight < SyncPreviewJob.ChunkSize);
    }

    /// <summary>
    /// A presence read that could not be answered part way through leaves no slot written.
    /// </summary>
    /// <remarks>
    /// Three counts arrive together or not at all. A site put in the not-yet-there column because
    /// its read failed is a number a reader cannot tell from a real one, so the whole count fails and
    /// the read route answers no view.
    /// </remarks>
    [Fact]
    public async Task APresenceReadThatFailedPartWayThroughLeavesNoSlot()
    {
        var cache = new SyncPreviewCache(TimeProvider.System);
        var asked = 0;

        Task<bool> AskAsync(string identity, CancellationToken ct)
        {
            asked++;
            if (asked == 3)
            {
                throw new HttpRequestException("nothing answered");
            }

            return Task.FromResult(false);
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CountAsync(Sites(4), AskAsync, cache));

        Assert.Equal(3, asked);
        Assert.Null(cache.Held(WhisparrGeneration.V2));
    }

    /// <summary>
    /// None of the three sync routes answers the keeps-no-scene-records refusal on a generation that
    /// registers sites.
    /// </summary>
    /// <remarks>
    /// The refusal was the whole of what that generation used to get. It stays as an enum member for
    /// a target obtaining neither role, and is no longer the answer for one obtaining the site add.
    /// </remarks>
    [Fact]
    public async Task TheThreeRoutesNoLongerRefuseAGenerationThatRegistersSites()
    {
        await using var host = await SiteHost(held: false);

        var startedCount = await PostAsync<SyncEnqueued>(host, "sync/preview");
        var read = await ReadCountAsync(host);
        var startedRun = await PostAsync<SyncEnqueued>(host, "sync/run");

        Assert.Equal(SyncRefusalKind.None, startedCount.Refusal);
        Assert.Equal(SyncRefusalKind.None, read.Refusal);
        Assert.Equal(SyncRefusalKind.None, startedRun.Refusal);
        Assert.NotNull(startedCount.JobId);
        Assert.NotNull(startedRun.JobId);
    }

    /// <summary>
    /// With the monitor toggle off, nothing reads a scene number and nothing sets a flag.
    /// </summary>
    /// <remarks>
    /// Over a library that would otherwise produce many of both, and proved by both doubles refusing
    /// a call they were not given rather than by a zero count: the provider throws on an identifier
    /// it holds no answer for, and the recording client throws on a verb it was given no answer for,
    /// so either call faults the run instead of passing unnoticed.
    /// </remarks>
    [Fact]
    public async Task WithTheToggleOffNoSceneNumberIsReadAndNoFlagIsSet()
    {
        var provider = new RecordingProviderCatalogue(NoAnswers);
        await using var host = await SiteHost(held: false, provider);
        await SeedSiteAsync(host, FirstSite, FirstScene);
        await SeedSiteAsync(host, SecondSite, SecondScene);

        var progress = await RunAsync(host, alsoMonitor: false);

        Assert.Empty(provider.Resolved);
        Assert.DoesNotContain(nameof(IWhisparrSiteSceneReading.ReduceSiteSceneRowsAsync), host.Client.Verbs);
        Assert.DoesNotContain(nameof(IWhisparrSceneMonitorActing.SetSceneMonitoredAsync), host.Client.Verbs);
        Assert.Empty(host.Client.UnexpectedCalls);
        Assert.Contains("2 sites registered", Assert.Single(progress.Summaries), StringComparison.Ordinal);
    }

    /// <summary>
    /// With the toggle on, every scene the reader owns on a registered site is flagged, including one
    /// on a site the instance already held.
    /// </summary>
    /// <remarks>
    /// That last one is the difference between marking what was just registered and marking what the
    /// reader owns, which is what the requirement asks for: on a second press most of the library is
    /// on sites a previous run registered.
    /// </remarks>
    [Fact]
    public async Task WithTheToggleOnEveryOwnedSceneIsFlaggedIncludingOnASiteAlreadyHeld()
    {
        var provider = ProviderNaming(FirstScene, SecondScene);
        await using var host = await MonitoringHost(provider);
        await SeedSiteAsync(host, FirstSite, FirstScene);
        await SeedSiteAsync(host, SecondSite, SecondScene);

        var progress = await RunAsync(host, alsoMonitor: true);

        Assert.Equal(
            new[] { FirstScene, SecondScene }.Order(),
            provider.Resolved.Order());
        Assert.Equal([RegisteredSiteId, HeldSiteId], host.Client.SiteSceneReads.Select(read => read.SiteId));
        Assert.Equal(
            new[] { RowFor(FirstScene), RowFor(SecondScene) }.Order(),
            Verb(host, nameof(IWhisparrSceneMonitorActing.SetSceneMonitoredAsync))
                .Select(call => call.EntityId!.Value)
                .Order());
        Assert.All(
            Verb(host, nameof(IWhisparrSceneMonitorActing.SetSceneMonitoredAsync)),
            call => Assert.True(call.Monitored));

        var summary = Assert.Single(progress.Summaries);
        Assert.Contains("1 already in Whisparr", summary, StringComparison.Ordinal);
        Assert.Contains("2 scenes monitored", summary, StringComparison.Ordinal);
    }

    private static List<LibrarySiteIdentity> Sites(int count)
        => [.. Enumerable.Range(1, count).Select(
            n => new LibrarySiteIdentity(n, $"{n:x8}-0000-4000-8000-000000000000"))];

    private static async Task<SyncPreviewView?> CountAsync(
        IReadOnlyList<LibrarySiteIdentity> sites,
        Func<string, CancellationToken, Task<bool>> presence,
        SyncPreviewCache? cache = null)
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<ILibrarySceneIdentityPort>(
                StubLibraryIdentities.OfSites(sites, unidentified: 3))
            .AddSingleton(cache ?? new SyncPreviewCache(TimeProvider.System))
            .BuildServiceProvider();

        return await SyncPreviewJob.RunAsync(
            provider.GetRequiredService<IServiceScopeFactory>(),
            (_, _) => Task.FromResult<SyncPreviewAiming?>(
                new SyncPreviewAiming(
                    WhisparrGeneration.V2, SyncRegisters.Sites, Held: null, presence)),
            NullLogger.Instance,
            TestCt);
    }

    /// <summary>One request answering <paramref name="answer"/>, recording what it was asked about.</summary>
    private static Func<string, CancellationToken, Task<WhisparrResponse?>> Answering(
        List<string> asked, WhisparrResponse answer)
        => (identity, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            asked.Add(identity);
            return Task.FromResult<WhisparrResponse?>(answer);
        };

    /// <summary>
    /// A host on v2, whose instance either holds every site or holds none.
    /// </summary>
    /// <remarks>
    /// The add is given an answer only where the instance holds nothing. A pass that composed one
    /// against an instance that already holds the site reaches a verb this client was given no answer
    /// for, and the client refuses it.
    /// </remarks>
    private static async Task<MonitorHost> SiteHost(
        bool held, RecordingProviderCatalogue? provider = null)
    {
        var host = await MonitorHost.CreateAsync(
            generation: WhisparrGeneration.V2, catalogue: provider);
        host.Client.Answering(
            nameof(IWhisparrStudioActing.ReadStudioAsync),
            held ? MonitorHost.Json(200, HeldRow) : MonitorHost.Json(404, string.Empty));

        if (!held)
        {
            host.Client.Answering(
                nameof(IWhisparrSiteRegistrationActing.RegisterSiteAsync),
                MonitorHost.Json(201, RegisteredRow));
        }

        return host;
    }

    /// <summary>
    /// A host whose instance holds the second site the run reaches and not the first, and which
    /// answers the scene rows and the flag.
    /// </summary>
    /// <remarks>
    /// Two answers are queued for the presence read, so one site is registered by this run and the
    /// other was already there. Which library studio each is depends on the order the identifier
    /// stream yields them, so every assertion reads the instance's own site ids rather than assuming
    /// one.
    /// </remarks>
    private static async Task<MonitorHost> MonitoringHost(RecordingProviderCatalogue provider)
    {
        var host = await MonitorHost.CreateAsync(
            generation: WhisparrGeneration.V2, catalogue: provider);

        host.Client
            .Answering(
                nameof(IWhisparrStudioActing.ReadStudioAsync),
                MonitorHost.Json(404, string.Empty),
                MonitorHost.Json(200, HeldRow))
            .Answering(
                nameof(IWhisparrSiteRegistrationActing.RegisterSiteAsync),
                MonitorHost.Json(201, RegisteredRow))
            .Answering(
                nameof(IWhisparrSceneMonitorActing.SetSceneMonitoredAsync),
                MonitorHost.Json(202, "{}"));

        foreach (var (scene, number) in SceneNumbers)
        {
            host.Client.SiteSceneRowIds[number] = SceneRows[scene];
        }

        return host;
    }

    /// <summary>A provider naming a number for each of <paramref name="scenes"/> and nothing else.</summary>
    private static RecordingProviderCatalogue ProviderNaming(params string[] scenes)
        => new(scenes.ToDictionary(
            scene => scene, scene => (int?)SceneNumbers[scene], StringComparer.Ordinal));

    /// <summary>The instance's own row identifier for <paramref name="scene"/>.</summary>
    private static int RowFor(string scene) => SceneRows[scene];

    /// <summary>Seeds one studio the library identifies, holding one scene it identifies.</summary>
    private static async Task SeedSiteAsync(MonitorHost host, string site, string scene)
    {
        var studioId = await host.SeedStudioAsync(V2Endpoint, site);
        await host.SeedStudioSceneAsync(studioId, V2Endpoint, scene);
    }

    private static IEnumerable<ActingCall> Verb(MonitorHost host, string verb)
        => host.Client.Acting.Where(call => call.Verb == verb);

    private static async Task<RecordingJobProgress> RunAsync(
        MonitorHost host, bool alsoMonitor = false)
    {
        await PostAsync<SyncEnqueued>(
            host, "sync/run", alsoMonitor ? """{"alsoMonitor":true}""" : "{}");
        var progress = new RecordingJobProgress();
        await host.RunEnqueuedBatchAsync(progress);
        return progress;
    }

    private static async Task<SyncPreviewRead> ReadCountAsync(MonitorHost host)
    {
        var answered = await host.Http.GetAsync(
            "/api/extensions/" + host.ExtensionId + "/sync/preview", TestCt);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<SyncPreviewRead>(TestCt))!;
    }

    /// <summary>Posts <paramref name="body"/> to <paramref name="route"/>.</summary>
    /// <remarks>
    /// The default body names the monitor toggle not at all, which reads as off: a caller naming
    /// nothing monitors nothing, which is the same request a reader with the switch off makes.
    /// </remarks>
    private static async Task<T> PostAsync<T>(MonitorHost host, string route, string body = "{}")
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var answered = await host.Http.PostAsync(
            "/api/extensions/" + host.ExtensionId + "/" + route, content, TestCt);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<T>(TestCt))!;
    }
}
