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

    /// <summary>The instance root this studio's own files agree on.</summary>
    private const string AgreedRoot = "/i-downloads-p/videos";

    /// <summary>The instance root a site was registered at before any of this ran.</summary>
    private const string OtherRoot = "/g-downloads-p/videos";

    /// <summary>The library root holding a studio's files that its site was not registered at.</summary>
    private const string LeftBehindRoot = "G:/Downloads/P";

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
            NeverMoves,
            NeverRefreshes,
            agreedRoot: null,
            new LibrarySiteIdentity(4, FirstSite),
            TestCt);

        var alreadyThere = await SiteRegistrationStep.RegisterAsync(
            Answering(reads, RecordingWhisparrClient.Json(200, HeldRow)),
            Answering(adds, RecordingWhisparrClient.Json(201, RegisteredRow)),
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
            NeverMoves,
            NeverRefreshes,
            agreedRoot: null,
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
    /// Seeded past <see cref="SyncPreviewJob.ChunkSize"/>, so the count both spans more than one
    /// batch and reddens on a ceiling introduced at any value below the seed.
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

    /// <summary>The pacing bound is a bound on one site's scene reads in flight, and it is one.</summary>
    /// <remarks>
    /// Read from the constant rather than restated: what it bounds is how many of a site's own scene
    /// reads are outstanding, and not how many the pass issues.
    /// </remarks>
    [Fact]
    public void ThePacingBoundIsOneReadInFlightAndBoundsNoTotal()
    {
        Assert.Equal(1, SyncPreviewJob.SiteSceneReadsInFlight);
        Assert.True(SyncPreviewJob.SiteSceneReadsInFlight < SyncPreviewJob.ChunkSize);
    }

    /// <summary>
    /// A site read that could not be answered part way through leaves no slot written.
    /// </summary>
    /// <remarks>
    /// Three counts arrive together or not at all. A site put in the not-yet-there column because
    /// its read failed is a number a reader cannot tell from a real one, so the whole count fails and
    /// the read route answers no view.
    /// </remarks>
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

    /// <summary>
    /// A site the instance holds at a root other than the agreed one is moved once, and no add is
    /// composed for it.
    /// </summary>
    /// <remarks>
    /// The correction is one update against the site the read already found. A second add would
    /// create a duplicate the instance has no way to merge.
    /// </remarks>
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

    /// <summary>A site already at the agreed root is sent nothing at all.</summary>
    [Fact]
    public async Task ASiteHeldAtTheAgreedRootIsSentNothing()
    {
        var instance = new InstanceHolding((FirstSite, HeldSiteId, AgreedRoot));

        var outcome = await PassAsync(instance, FirstSite, AgreedRoot, TestCt);

        Assert.Equal(SceneRegistration.AlreadyHeld, outcome.Registration);
        Assert.Empty(instance.Moves);
        Assert.Empty(instance.Adds);
    }

    /// <summary>
    /// A site held at the agreed root, spelled with the separator its own host uses, is sent
    /// nothing.
    /// </summary>
    /// <remarks>
    /// The agreed root reaches the step forward-slashed whichever host the instance runs on, because
    /// every candidate the addressing port builds is spelled that way. Compared literally, a Windows
    /// instance would be told to move every site it holds on every run, forever.
    /// </remarks>
    [Fact]
    public async Task ASiteHeldAtTheAgreedRootTheInstanceSpellsItsOwnWayIsSentNothing()
    {
        var instance = new InstanceHolding((FirstSite, HeldSiteId, @"D:\Media"));

        var outcome = await PassAsync(instance, FirstSite, "D:/Media", TestCt);

        Assert.Equal(SceneRegistration.AlreadyHeld, outcome.Registration);
        Assert.Empty(instance.Moves);
        Assert.Empty(instance.Adds);
    }

    /// <summary>
    /// A move whose catalogue re-read never arrived is finished by the next pass, which then stops.
    /// </summary>
    /// <remarks>
    /// The update lands before the re-read, so a timeout between the two leaves the site registered
    /// at the right root and reporting no file. Its root reads as correct from then on, so without a
    /// re-read decided on its own the site would stay unlinked with nothing able to repair it.
    /// </remarks>
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

    /// <summary>A studio this product has no agreed root for is left where it is.</summary>
    /// <remarks>
    /// A studio owning no file reaches this step with no root, because there is nothing to derive one
    /// from. A studio whose own library root the instance agrees no spelling for never reaches it at
    /// all: the composition refuses before the read, which
    /// <c>SiteRootRegistrationTests.AStudioWhoseRootAgreedOnNothingHasNoAddSentForIt</c> asserts over
    /// the whole pass. Both are states in which moving the site would be a guess written to a live
    /// instance.
    /// </remarks>
    [Fact]
    public async Task AStudioWithNoAgreedRootIsSentNothing()
    {
        var instance = new InstanceHolding((FirstSite, HeldSiteId, OtherRoot));

        var outcome = await PassAsync(instance, FirstSite, agreedRoot: null, TestCt);

        Assert.Equal(SceneRegistration.AlreadyHeld, outcome.Registration);
        Assert.Empty(instance.Moves);
        Assert.Empty(instance.Adds);
    }

    /// <summary>A second pass over what the first one left sends nothing of either kind.</summary>
    /// <remarks>
    /// The whole point of correcting a root by moving rather than adding: a library already put right
    /// costs one read per site and changes nothing.
    /// </remarks>
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

    /// <summary>A pass stopped part way leaves every site it already moved at its new root.</summary>
    /// <remarks>
    /// There is nothing to undo. Each move is its own request against its own site, so a stop leaves
    /// a library part corrected rather than one in a state no run produced.
    /// </remarks>
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

    /// <summary>A move the instance declined is reported as refused, not as already held.</summary>
    /// <remarks>
    /// The site is still registered where none of its files sit, which is the state this pass exists
    /// to remove, so reporting it as untouched would hide a failure a reader acts on.
    /// </remarks>
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

    /// <summary>
    /// The unit for a site the pass moved is completed as succeeded, and the refused site's is not.
    /// </summary>
    /// <remarks>
    /// Read off the unit the run actually reported rather than off the member that decides it: the
    /// host aggregates what it was told, and a moved site reported as failed is the figure a reader
    /// would act on.
    /// </remarks>
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

    /// <summary>The pass counts a site it moved apart from the ones it refused.</summary>
    /// <remarks>
    /// A moved site is neither work refused nor a catalogue that was already right, and a reader acts
    /// differently on each.
    /// </remarks>
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

    /// <summary>
    /// A run over a library with one split studio names the other root once and carries both counts.
    /// </summary>
    /// <remarks>
    /// The line says the files were left where they are, because an operator reading that a site
    /// moved could otherwise take it to mean the files moved with it.
    /// </remarks>
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

    /// <summary>A run with no split studio says nothing about splits at all.</summary>
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

    /// <summary>A run that moved sites states how many.</summary>
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

    /// <summary>
    /// A run that left studios alone for want of an agreed root states that count apart.
    /// </summary>
    /// <remarks>
    /// Inside the refused figure rather than beside it, because those studios are counted in it.
    /// </remarks>
    [Fact]
    public async Task ARunThatLeftStudiosAloneForWantOfAnAgreedRootStatesThatCountApart()
    {
        var progress = new RecordingJobProgress();

        var run = await RunOverAsync(
            progress,
            (SceneRegistration.Refused, NoAgreedRoot),
            (SceneRegistration.Refused, AtOneRoot));

        Assert.Equal(2, run.Refused);
        Assert.Equal(1, run.WithoutAnAgreedRoot);
        Assert.Contains(
            "2 refused (1 for want of an agreed root)",
            Assert.Single(progress.Summaries),
            StringComparison.Ordinal);
    }

    /// <summary>Two studios split under one root name that root once, not twice.</summary>
    /// <remarks>
    /// The names are what an operator acts on, and one entry per library root is the only shape that
    /// does not grow with the library.
    /// </remarks>
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

    /// <summary>A studio whose files all sit under the one root that was chosen.</summary>
    private static EntityRoot AtOneRoot { get; } =
        new(AgreedRoot, MonitorRefusalKind.None, "I:/Downloads/P", 4, 0, []);

    /// <summary>A studio whose own library root the instance agreed no spelling for.</summary>
    private static EntityRoot NoAgreedRoot { get; } =
        new(null, MonitorRefusalKind.NoAgreedRootForThisEntity, "I:/Downloads/P", 4, 0, []);

    /// <summary>A studio with files under a library root other than the one that was chosen.</summary>
    private static EntityRoot Split(int filesLeftElsewhere, params string[] leftBehind)
        => new(AgreedRoot, MonitorRefusalKind.None, "I:/Downloads/P", 4, filesLeftElsewhere, leftBehind);

    /// <summary>One site through the registration step, against <paramref name="instance"/>.</summary>
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

    /// <summary>One run over as many sites as <paramref name="answers"/> names.</summary>
    private static Task<SyncLibraryRun> RunOverAsync(
        RecordingJobProgress progress, params SceneRegistration[] answers)
        => RunOverAsync(progress, [.. answers.Select(answer => (answer, AtOneRoot))]);

    /// <summary>
    /// One run over as many sites as <paramref name="answers"/> names, each with the root choice
    /// that was made for it.
    /// </summary>
    private static Task<SyncLibraryRun> RunOverAsync(
        RecordingJobProgress progress,
        params (SceneRegistration Registration, EntityRoot Root)[] answers)
    {
        var offered = 0;

        return SyncLibraryPlanner.RunAsync(
            SyncRegisters.Sites,
            Streamed,
            identity => identity,
            (_, _) =>
            {
                var (registration, root) = answers[offered++];
                return Task.FromResult(
                    new SyncRegistration(
                        registration, MonitorHost.Json(202, "{}"), HeldSiteId, root));
            },
            monitor: null,
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

    /// <summary>A move no case under it may make, so a call faults rather than passing unnoticed.</summary>
    private static Task<WhisparrResponse?> NeverMoves(int siteId, string root, CancellationToken ct)
        => throw new InvalidOperationException("This case must send no move.");

    /// <summary>The same for the catalogue re-read.</summary>
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

    /// <summary>
    /// An instance holding some sites, each at one root, that a pass can read, add to and move.
    /// </summary>
    /// <remarks>
    /// Stateful on purpose. What a second pass sends depends on what the first one left, so a double
    /// answering a fixed reply could not tell a correction that stuck from one that did not.
    /// </remarks>
    private sealed class InstanceHolding
    {
        /// <summary>What a site whose catalogue the instance has read reports.</summary>
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

        /// <summary>
        /// The update lands and the catalogue re-read beside it does not, which is what a timeout or
        /// a restart between the two leaves behind.
        /// </summary>
        public bool MoveLosesTheCatalogueReRead { get; init; }

        /// <summary>Stopped once one move is made, standing in for the host stopping the job.</summary>
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

        /// <remarks>
        /// Serialized rather than interpolated, so a root carrying the separator a Windows instance
        /// answers with reaches the step as the instance really spells it.
        /// </remarks>
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
