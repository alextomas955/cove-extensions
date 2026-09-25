using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
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

// The recording client refuses a verb it was given no answer for, so a request this pass must not
// make faults the run instead of passing unnoticed.
public sealed class SyncLibrarySitesTests
{
    // The namespace v2 identifies a site in.
    private const string V2Endpoint = "https://theporndb.net/graphql";

    private const string FirstSite = "a30bc641-6afe-4c80-9c73-ecb68104a68d";

    private const string SecondSite = "44e8ac11-9ed4-42e5-a9f4-bc2c138a5a6e";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";

    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    // The number the metadata provider issues for each scene.
    private static readonly Dictionary<string, int> SceneNumbers = new(StringComparer.Ordinal)
    {
        [FirstScene] = 1363738,
        [SecondScene] = 1363739,
    };

    // The instance's own row identifier for each scene, held apart from the provider's number. A
    // pass that set the flag by the provider's number would pass against one shared value.
    private static readonly Dictionary<string, int> SceneRows = new(StringComparer.Ordinal)
    {
        [FirstScene] = 77,
        [SecondScene] = 88,
    };

    // A provider holding no answer at all, so any resolution faults the run.
    private static readonly Dictionary<string, int?> NoAnswers = new(StringComparer.Ordinal);

    private const int RegisteredSiteId = 11;

    private const int HeldSiteId = 9;

    // The instance root this studio's own files agree on.
    private const string AgreedRoot = "/i-downloads-p/videos";

    // The instance root a site was registered at before the run.
    private const string OtherRoot = "/g-downloads-p/videos";

    // Cove's own spelling of a library root, which the instance never uses.
    private const string LeftBehindRoot = "G:/Downloads/P";

    private static readonly string RegisteredRow =
        $$"""{"id":{{RegisteredSiteId}},"title":"Jay Bank Presents"}""";

    private static readonly string HeldRow =
        $$"""{"id":{{HeldSiteId}},"title":"Jay Bank Presents"}""";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // Scoped to the toggle being off. With it on the pass marks the scenes on a site, which is a
    // separate capability. The absence is proved by the recording client refusing a verb it was
    // given no answer for, because a zero count also passes over a run that walked nothing.
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

    // This is what makes a second pass create no duplicate. The recording client is given no answer
    // for the add, so composing one faults the run.
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

    // The id comes off whichever answer the step already read: the add's where the site was added,
    // the held row's where it was already there. Nothing reads the site a second time.
    [Fact]
    public async Task ThePerSiteOutcomeCarriesTheInstancesOwnIdWithNoSecondRead()
    {
        var reads = new List<string>();
        var adds = new List<string>();

        var registered = await SiteRegistrationStep.RegisterAsync(
            Answering(reads, RecordingWhisparrCore.Json(404, string.Empty)),
            Answering(adds, RecordingWhisparrCore.Json(201, RegisteredRow)),
            NeverMoves,
            NeverRefreshes,
            agreedRoot: null,
            new LibrarySiteIdentity(4, FirstSite),
            TestCt);

        var alreadyThere = await SiteRegistrationStep.RegisterAsync(
            Answering(reads, RecordingWhisparrCore.Json(200, HeldRow)),
            Answering(adds, RecordingWhisparrCore.Json(201, RegisteredRow)),
            NeverMoves,
            NeverRefreshes,
            agreedRoot: null,
            new LibrarySiteIdentity(7, SecondSite),
            TestCt);

        Assert.Equal(SceneRegistration.Registered, registered.Registration);
        Assert.Equal(RegisteredSiteId, registered.InstanceId);
        Assert.Equal(SceneRegistration.AlreadyHeld, alreadyThere.Registration);
        Assert.Equal(HeldSiteId, alreadyThere.InstanceId);

        Assert.Equal([FirstSite, SecondSite], reads);
        Assert.Equal([FirstSite], adds);
    }

    // Registering on an answer nothing could be read out of would add a site the instance may
    // already hold, and this generation publishes no contract for what that answer then is.
    [Fact]
    public async Task ASiteWhoseReadAnsweredNeitherIsRefusedRatherThanRegistered()
    {
        var adds = new List<string>();

        var outcome = await SiteRegistrationStep.RegisterAsync(
            (_, _) => Task.FromResult<WhisparrResponse?>(null),
            Answering(adds, RecordingWhisparrCore.Json(201, RegisteredRow)),
            NeverMoves,
            NeverRefreshes,
            agreedRoot: null,
            new LibrarySiteIdentity(4, FirstSite),
            TestCt);

        Assert.Equal(SceneRegistration.Refused, outcome.Registration);
        Assert.Null(outcome.InstanceId);
        Assert.Empty(adds);
    }

    // Seeded past SyncPreviewJob.ChunkSize, so the count spans more than one chunk and fails on a
    // ceiling introduced at any value below the seed. A ceiling would answer a short already-there
    // and not-yet-there pair that reads like a complete one, so the pair is asserted to add back up
    // to the number seeded.
    [Fact]
    public async Task TheSiteCountReadsEverySiteInTheStreamAndTruncatesNothing()
    {
        var seeded = Sites(SyncPreviewJob.ChunkSize + 1);
        var asked = new List<string>();

        var counted = await CountAsync(
            seeded,
            (batch, _) =>
            {
                asked.AddRange(batch);
                return Task.FromResult(
                    new SiteBatchReading(
                        batch.Where((_, index) => index % 2 == 1).ToHashSet(StringComparer.Ordinal),
                        new HashSet<string>(StringComparer.Ordinal)));
            });

        Assert.NotNull(counted);
        Assert.Equal(seeded.Count, asked.Count);
        Assert.Equal(seeded.Count, counted.AlreadyThere + counted.NotYetThere);
        Assert.Equal(SyncRegisters.Sites, counted.Registers);
    }

    // The bound is on how many of one site's scene reads are outstanding, not on how many the pass
    // issues. It is read from the constant rather than restated.
    [Fact]
    public void ThePacingBoundIsOneReadInFlightAndBoundsNoTotal()
    {
        Assert.Equal(1, SyncPreviewJob.SiteSceneReadsInFlight);
        Assert.True(SyncPreviewJob.SiteSceneReadsInFlight < SyncPreviewJob.ChunkSize);
    }

    // The three counts arrive together or not at all. A site put in the not-yet-there column
    // because its read failed is a number a reader cannot tell from a real one.
    [Fact]
    public async Task ASiteReadThatFailedPartWayThroughLeavesNoSlot()
    {
        var cache = new SyncPreviewCache(TimeProvider.System);
        var asked = 0;

        Task<SiteBatchReading> AskAsync(IReadOnlyCollection<string> batch, CancellationToken ct)
        {
            asked++;
            return asked == 2
                ? throw new HttpRequestException("nothing answered")
                : Task.FromResult(
                    new SiteBatchReading(
                        new HashSet<string>(StringComparer.Ordinal),
                        new HashSet<string>(StringComparer.Ordinal)));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CountAsync(Sites(SyncPreviewJob.ChunkSize + 2), AskAsync, cache));

        Assert.Equal(2, asked);
        Assert.Null(cache.Held(WhisparrGeneration.V2));
    }

    // The refusal stays as an enum member for a target holding neither role. It is not the answer
    // for one holding the site add.
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

    // Proved by both doubles refusing a call they were not given rather than by a zero count. The
    // provider throws on an identifier it holds no answer for and the client throws on a verb it
    // was given no answer for, so either call faults the run.
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

    // The site the instance already held is the difference between marking what this run
    // registered and marking what the reader owns. On a second press most of the library sits on
    // sites an earlier run registered.
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

    // The correction is one update against the site the read already found. A second add would
    // create a duplicate the instance has no way to merge.
    [Fact]
    public async Task ASiteHeldAtTheWrongRootIsMovedOnceAndAddedNotAtAll()
    {
        var instance = new InstanceHolding((FirstSite, HeldSiteId, OtherRoot));

        var outcome = await PassAsync(instance, FirstSite, AgreedRoot, TestCt);

        Assert.Equal(SceneRegistration.Moved, outcome.Registration);
        Assert.Equal(HeldSiteId, outcome.InstanceId);
        Assert.Equal([(HeldSiteId, AgreedRoot)], instance.Moves);
        Assert.Empty(instance.Adds);
    }

    [Fact]
    public async Task ASiteHeldAtTheAgreedRootIsSentNothing()
    {
        var instance = new InstanceHolding((FirstSite, HeldSiteId, AgreedRoot));

        var outcome = await PassAsync(instance, FirstSite, AgreedRoot, TestCt);

        Assert.Equal(SceneRegistration.AlreadyHeld, outcome.Registration);
        Assert.Empty(instance.Moves);
        Assert.Empty(instance.Adds);
    }

    // Every candidate the addressing port builds is forward-slashed, whichever host the instance
    // runs on. Compared literally, a Windows instance would be told to move every site it holds on
    // every run.
    [Fact]
    public async Task ASiteHeldAtTheAgreedRootTheInstanceSpellsItsOwnWayIsSentNothing()
    {
        var instance = new InstanceHolding((FirstSite, HeldSiteId, @"D:\Media"));

        var outcome = await PassAsync(instance, FirstSite, "D:/Media", TestCt);

        Assert.Equal(SceneRegistration.AlreadyHeld, outcome.Registration);
        Assert.Empty(instance.Moves);
        Assert.Empty(instance.Adds);
    }

    // The update lands before the re-read, so a timeout between the two leaves the site registered
    // at the right root and reporting no file. Its root reads as correct from then on, so without a
    // re-read decided on its own the site would stay unlinked.
    [Fact]
    public async Task AMoveWhoseCatalogueReReadDidNotArriveIsFinishedByTheNextPass()
    {
        var instance = new InstanceHolding((FirstSite, HeldSiteId, OtherRoot))
        {
            MoveLosesTheCatalogueReRead = true,
        };

        var interrupted = await PassAsync(instance, FirstSite, AgreedRoot, TestCt);
        var repairing = await PassAsync(instance, FirstSite, AgreedRoot, TestCt);
        var settled = await PassAsync(instance, FirstSite, AgreedRoot, TestCt);

        Assert.Equal(SceneRegistration.Refused, interrupted.Registration);
        Assert.Equal(AgreedRoot, instance.RootOf(FirstSite));
        Assert.Equal(SceneRegistration.AlreadyHeld, repairing.Registration);
        Assert.Equal(SceneRegistration.AlreadyHeld, settled.Registration);
        Assert.Equal([HeldSiteId], instance.Refreshes);
        Assert.Equal([(HeldSiteId, AgreedRoot)], instance.Moves);
        Assert.Empty(instance.Adds);
    }

    // A studio owning no file reaches this step with no root, because there is nothing to derive
    // one from. Moving its site would be a guess written to a live instance.
    [Fact]
    public async Task AStudioWithNoAgreedRootIsSentNothing()
    {
        var instance = new InstanceHolding((FirstSite, HeldSiteId, OtherRoot));

        var outcome = await PassAsync(instance, FirstSite, agreedRoot: null, TestCt);

        Assert.Equal(SceneRegistration.AlreadyHeld, outcome.Registration);
        Assert.Empty(instance.Moves);
        Assert.Empty(instance.Adds);
    }

    // Correcting a root by moving rather than adding means a library already put right costs one
    // read per site and changes nothing.
    [Fact]
    public async Task ASecondPassOverACorrectedLibrarySendsNothing()
    {
        var instance = new InstanceHolding(
            (FirstSite, HeldSiteId, OtherRoot), (SecondSite, RegisteredSiteId, OtherRoot));

        await PassAsync(instance, FirstSite, AgreedRoot, TestCt);
        await PassAsync(instance, SecondSite, AgreedRoot, TestCt);
        var movedByTheFirstPass = instance.Moves.Count;

        await PassAsync(instance, FirstSite, AgreedRoot, TestCt);
        await PassAsync(instance, SecondSite, AgreedRoot, TestCt);

        Assert.Equal(2, movedByTheFirstPass);
        Assert.Equal(movedByTheFirstPass, instance.Moves.Count);
        Assert.Empty(instance.Adds);
    }

    // Each move is its own request against its own site, so a stop leaves a library part corrected
    // rather than one in a state no run produced. There is nothing to undo.
    [Fact]
    public async Task APassStoppedPartWayLeavesTheSitesItMovedAtTheirNewRoot()
    {
        using var stopping = new CancellationTokenSource();
        var instance = new InstanceHolding(
            (FirstSite, HeldSiteId, OtherRoot), (SecondSite, RegisteredSiteId, OtherRoot))
        {
            StopAfterTheFirstMove = stopping,
        };

        await PassAsync(instance, FirstSite, AgreedRoot, stopping.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PassAsync(instance, SecondSite, AgreedRoot, stopping.Token));

        Assert.Equal(AgreedRoot, instance.RootOf(FirstSite));
        Assert.Equal(OtherRoot, instance.RootOf(SecondSite));
    }

    // The site is still registered where none of its files sit, so reporting it as untouched would
    // hide a failure a reader acts on.
    [Fact]
    public async Task AMoveTheInstanceDeclinedIsReportedAsRefused()
    {
        var instance = new InstanceHolding((FirstSite, HeldSiteId, OtherRoot))
        {
            RefusesTheMove = true,
        };

        var outcome = await PassAsync(instance, FirstSite, AgreedRoot, TestCt);

        Assert.Equal(SceneRegistration.Refused, outcome.Registration);
        Assert.Equal(OtherRoot, instance.RootOf(FirstSite));
    }

    // Read off the unit the run reported rather than off the member that decides it. The host
    // aggregates what it was told, and a moved site reported as failed is what a reader acts on.
    [Fact]
    public async Task TheUnitForASiteThePassMovedIsSucceededAndTheRefusedOneIsNot()
    {
        var progress = new RecordingJobProgress();

        var run = await RunOverAsync(progress, SceneRegistration.Moved, SceneRegistration.Refused);

        Assert.Equal(
            [JobUnitOutcome.Succeeded, JobUnitOutcome.Failed],
            progress.Units.Select(unit => unit.Outcome));
        Assert.Equal(1, run.Moved);
        Assert.Equal(1, run.Refused);
    }

    // A moved site is neither work refused nor a catalogue that was already right.
    [Fact]
    public async Task ThePassCountsAMovedSiteApartFromTheRefusedOnes()
    {
        var progress = new RecordingJobProgress();

        var run = await RunOverAsync(
            progress,
            SceneRegistration.Moved,
            SceneRegistration.Moved,
            SceneRegistration.Refused,
            SceneRegistration.AlreadyHeld);

        Assert.Equal(2, run.Moved);
        Assert.Equal(1, run.Refused);
        Assert.Equal(1, run.AlreadyHeld);
        Assert.Equal(0, run.Registered);
        Assert.Contains(
            "2 moved to the root holding their files",
            Assert.Single(progress.Summaries),
            StringComparison.Ordinal);
    }

    // The line says the files were left where they are. An operator reading that a site moved could
    // otherwise take it to mean the files moved with it.
    [Fact]
    public async Task ARunWithOneSplitStudioNamesTheOtherRootOnceAndCarriesBothCounts()
    {
        var progress = new RecordingJobProgress();

        var run = await RunOverAsync(
            progress,
            (SceneRegistration.Moved, Split(filesLeftElsewhere: 7, LeftBehindRoot)),
            (SceneRegistration.AlreadyHeld, AtOneRoot));

        var summary = Assert.Single(progress.Summaries);

        Assert.Equal(1, run.SplitAcrossRoots);
        Assert.Equal(7, run.FilesLeftElsewhere);
        Assert.Equal([LeftBehindRoot], run.RootsLeftBehind);
        Assert.Contains(
            "1 site has files under more than one library root", summary, StringComparison.Ordinal);
        Assert.Contains(
            "7 files were left where they are under " + LeftBehindRoot,
            summary,
            StringComparison.Ordinal);
        Assert.Contains("nothing was copied", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARunWithNoSplitStudioSaysNothingAboutSplits()
    {
        var progress = new RecordingJobProgress();

        var run = await RunOverAsync(
            progress,
            (SceneRegistration.Registered, AtOneRoot),
            (SceneRegistration.AlreadyHeld, AtOneRoot));

        var summary = Assert.Single(progress.Summaries);

        Assert.Equal(0, run.SplitAcrossRoots);
        Assert.Empty(run.RootsLeftBehind);
        Assert.Equal(
            "1 sites registered, 1 already in Whisparr, 0 refused.", summary, StringComparer.Ordinal);
    }

    [Fact]
    public async Task ARunThatMovedSitesStatesHowMany()
    {
        var progress = new RecordingJobProgress();

        await RunOverAsync(
            progress, (SceneRegistration.Moved, AtOneRoot), (SceneRegistration.Moved, AtOneRoot));

        Assert.Contains(
            "2 moved to the root holding their files",
            Assert.Single(progress.Summaries),
            StringComparison.Ordinal);
    }

    // The count sits beside the other figures rather than inside the refused one. A studio the
    // instance already holds is counted as already held even where its root was not agreed, so the
    // count is spread across two figures and a parenthetical inside either would read as a subset.
    [Fact]
    public async Task ARunThatAgreedNoRootForSomeStudiosStatesThatCountBesideTheOthers()
    {
        var progress = new RecordingJobProgress();

        var run = await RunOverAsync(
            progress,
            (SceneRegistration.Refused, NoAgreedRoot),
            (SceneRegistration.AlreadyHeld, NoAgreedRoot),
            (SceneRegistration.Refused, AtOneRoot));

        Assert.Equal(2, run.Refused);
        Assert.Equal(1, run.AlreadyHeld);
        Assert.Equal(2, run.WithoutAnAgreedRoot);
        Assert.Contains(
            "2 refused, 2 with no agreed root",
            Assert.Single(progress.Summaries),
            StringComparison.Ordinal);
    }

    // One entry per library root is the only shape that does not grow with the library.
    [Fact]
    public async Task TwoStudiosSplitUnderOneRootNameThatRootOnce()
    {
        var progress = new RecordingJobProgress();

        var run = await RunOverAsync(
            progress,
            (SceneRegistration.Moved, Split(filesLeftElsewhere: 3, LeftBehindRoot)),
            (SceneRegistration.Moved, Split(filesLeftElsewhere: 5, LeftBehindRoot)));

        Assert.Equal(2, run.SplitAcrossRoots);
        Assert.Equal(8, run.FilesLeftElsewhere);
        Assert.Equal([LeftBehindRoot], run.RootsLeftBehind);
        Assert.Contains(
            "2 sites have files under more than one library root",
            Assert.Single(progress.Summaries),
            StringComparison.Ordinal);
    }

    private static EntityRoot AtOneRoot { get; } =
        new(AgreedRoot, MonitorRefusalKind.None, "I:/Downloads/P", 4, 0, []);

    private static EntityRoot NoAgreedRoot { get; } =
        new(null, MonitorRefusalKind.NoAgreedRootForThisEntity, "I:/Downloads/P", 4, 0, []);

    private static EntityRoot Split(int filesLeftElsewhere, params string[] leftBehind)
        => new(AgreedRoot, MonitorRefusalKind.None, "I:/Downloads/P", 4, filesLeftElsewhere, leftBehind);

    private static Task<SyncRegistration> PassAsync(
        InstanceHolding instance,
        string site,
        string? agreedRoot,
        CancellationToken ct)
        => SiteRegistrationStep.RegisterAsync(
            instance.ReadAsync,
            instance.AddAsync,
            instance.MoveAsync,
            instance.RefreshAsync,
            agreedRoot,
            new LibrarySiteIdentity(4, site),
            ct);

    private static Task<SyncLibraryRun> RunOverAsync(
        RecordingJobProgress progress, params SceneRegistration[] answers)
        => RunOverAsync(progress, [.. answers.Select(answer => (answer, AtOneRoot))]);

    private static Task<SyncLibraryRun> RunOverAsync(
        RecordingJobProgress progress,
        params (SceneRegistration Registration, EntityRoot Root)[] answers)
    {
        var offered = 0;

        return SyncLibraryPlanner.RunAsync(
            SyncRegisters.Sites,
            new SyncLibrarySource<string>(
                Streamed,
                identity => identity,
                (_, _) => { var (registration, root) = answers[offered++]; return Task.FromResult(new SyncRegistration(registration, MonitorHost.Json(202, "{}"), HeldSiteId, root)); },
                null),
            progress,
            TestCt);

        async IAsyncEnumerable<string> Streamed([EnumeratorCancellation] CancellationToken ct)
        {
            for (var index = 0; index < answers.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                yield return string.Create(CultureInfo.InvariantCulture, $"site-{index}");
            }

            await Task.CompletedTask;
        }
    }

    private static Task<WhisparrResponse?> NeverMoves(int siteId, string root, CancellationToken ct)
        => throw new InvalidOperationException("This case must send no move.");

    private static Task<WhisparrResponse?> NeverRefreshes(int siteId, CancellationToken ct)
        => throw new InvalidOperationException("This case must send no catalogue re-read.");

    private static List<LibrarySiteIdentity> Sites(int count)
        => [.. Enumerable.Range(1, count).Select(
            n => new LibrarySiteIdentity(n, $"{n:x8}-0000-4000-8000-000000000000"))];

    private static async Task<SyncPreviewView?> CountAsync(
        IReadOnlyList<LibrarySiteIdentity> sites,
        Func<IReadOnlyCollection<string>, CancellationToken, Task<SiteBatchReading>> heldSites,
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
                    WhisparrGeneration.V2, SyncRegisters.Sites, Held: null, heldSites)),
            NullLogger.Instance,
            TestCt);
    }

    private static Func<string, CancellationToken, Task<WhisparrResponse?>> Answering(
        List<string> asked, WhisparrResponse answer)
        => (identity, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            asked.Add(identity);
            return Task.FromResult<WhisparrResponse?>(answer);
        };

    // The settings page offers a root only once something has recorded a reading for it. Without
    // this the run's ending sends the reader to a page listing nothing, and no site under that root
    // can ever acquire one.
    [Fact]
    public async Task APassThatSettledNoRootPutsThatRootOnTheSettingsPage()
    {
        await using var host = await UnsettledRootHostAsync();
        var studioId = await host.SeedStudioAsync(V2Endpoint, FirstSite);
        await host.SeedStudioFileAsync(studioId, UnsettledCoveRoot + "/Blue Harbor", 41);

        await RunAsync(host);

        var listed = Assert.Single(
            (await host.ReadFolderMappingsAsync()).Roots,
            root => root.Root == UnsettledCoveRoot);
        Assert.NotNull(listed.Refusal);
    }

    private const string UnsettledCoveRoot = "I:/Downloads/P";

    // The probe answers no file, so no library root is ever settled.
    private static Task<MonitorHost> UnsettledRootHostAsync()
        => MonitorHost.CreateAsync(
            generation: WhisparrGeneration.V2,
            bytes: BodyRecordingHandler.AnsweringByPath(path => path switch
            {
                var route when route.EndsWith("/filesystem", StringComparison.Ordinal)
                    => """{"parent":"/data/","directories":[],"files":[]}""",
                var route when route.EndsWith("/rootfolder", StringComparison.Ordinal)
                    => """[{"id":1,"path":"/data","accessible":true}]""",
                var route when route.EndsWith("/qualityprofile", StringComparison.Ordinal)
                    => """[{"id":1,"name":"Any"}]""",
                _ => "{}",
            }),
            libraryConfig: new CoveConfiguration
            {
                CovePaths = [new CovePath { Path = UnsettledCoveRoot }],
            });

    // The add is given an answer only where the instance holds nothing, so a pass that composed one
    // against a site the instance already holds reaches a verb the client refuses.
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

    // Two answers are queued for the presence read, so one site is registered by this run and the
    // other was already there. Which library studio each is depends on the order the identifier
    // stream yields them, so assertions read the instance's own site ids rather than assuming one.
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

    private static RecordingProviderCatalogue ProviderNaming(params string[] scenes)
        => new(scenes.ToDictionary(
            scene => scene, scene => (int?)SceneNumbers[scene], StringComparer.Ordinal));

    private static int RowFor(string scene) => SceneRows[scene];

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

    // The default body names the monitor toggle not at all, which reads as off.
    private static async Task<T> PostAsync<T>(MonitorHost host, string route, string body = "{}")
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var answered = await host.Http.PostAsync(
            "/api/extensions/" + host.ExtensionId + "/" + route, content, TestCt);
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<T>(TestCt))!;
    }

    // Stateful on purpose. What a second pass sends depends on what the first one left, so a double
    // answering a fixed reply could not tell a correction that stuck from one that did not.
    private sealed class InstanceHolding
    {
        private const int LinkedFiles = 2;

        private readonly Dictionary<string, (int Id, string Root, int Files)> _held;

        public InstanceHolding(params (string Site, int Id, string Root)[] held)
            => _held = held.ToDictionary(
                entry => entry.Site,
                entry => (entry.Id, entry.Root, LinkedFiles),
                StringComparer.Ordinal);

        public List<string> Adds { get; } = [];

        public List<(int SiteId, string Root)> Moves { get; } = [];

        public List<int> Refreshes { get; } = [];

        public bool RefusesTheMove { get; init; }

        // The update lands and the catalogue re-read beside it does not, which is what a timeout
        // or a restart between the two leaves behind.
        public bool MoveLosesTheCatalogueReRead { get; init; }

        // Stands in for the host stopping the job.
        public CancellationTokenSource? StopAfterTheFirstMove { get; init; }

        public string? RootOf(string site)
            => _held.TryGetValue(site, out var entry) ? entry.Root : null;

        public Task<WhisparrResponse?> ReadAsync(string site, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<WhisparrResponse?>(
                _held.TryGetValue(site, out var entry)
                    ? MonitorHost.Json(200, Row(entry.Id, entry.Root, entry.Files))
                    : MonitorHost.Json(404, string.Empty));
        }

        public Task<WhisparrResponse?> AddAsync(string site, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Adds.Add(site);
            _held[site] = (RegisteredSiteId, AgreedRoot, LinkedFiles);
            return Task.FromResult<WhisparrResponse?>(MonitorHost.Json(201, RegisteredRow));
        }

        public Task<WhisparrResponse?> MoveAsync(int siteId, string root, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Moves.Add((siteId, root));

            if (RefusesTheMove)
            {
                return Task.FromResult<WhisparrResponse?>(MonitorHost.Json(400, "[]"));
            }

            foreach (var (site, entry) in _held.Where(entry => entry.Value.Id == siteId).ToList())
            {
                _held[site] = (entry.Id, root, MoveLosesTheCatalogueReRead ? 0 : entry.Files);
            }

            StopAfterTheFirstMove?.Cancel();

            return Task.FromResult<WhisparrResponse?>(
                MoveLosesTheCatalogueReRead
                    ? MonitorHost.Json(503, "[]")
                    : MonitorHost.Json(202, "{}"));
        }

        public Task<WhisparrResponse?> RefreshAsync(int siteId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Refreshes.Add(siteId);

            foreach (var (site, entry) in _held.Where(entry => entry.Value.Id == siteId).ToList())
            {
                _held[site] = (entry.Id, entry.Root, LinkedFiles);
            }

            return Task.FromResult<WhisparrResponse?>(MonitorHost.Json(201, "{}"));
        }

        // Serialized rather than interpolated, so a root carrying the separator a Windows instance
        // answers with reaches the step as the instance really spells it.
        private static string Row(int siteId, string root, int files)
        {
            var separator = root.Contains('\\', StringComparison.Ordinal) ? "\\" : "/";

            return new JsonObject
            {
                ["id"] = siteId,
                ["title"] = "Jay Bank Presents",
                ["rootFolderPath"] = root,
                ["path"] = root + separator + "Jay Bank Presents",
                ["statistics"] = new JsonObject { ["episodeFileCount"] = files },
            }.ToJsonString();
        }
    }
}
