using System.Runtime.CompilerServices;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

public sealed class SyncLibraryPlannerTests
{
    private const string AcceptedFixture = "whisparr-v3-3.3.8.1097-scene-add-accepted.json";

    private const string AlreadyHeldFixture = "whisparr-v3-3.3.8.1097-scene-add-already-held.json";

    private const string RefusedFixture = "whisparr-v3-3.3.8.1097-scene-add-unknown-identifier.json";

    // A spelling of the connected namespace that differs from the stored one.
    private const string StandardStashDbAddress = "https://stashdb.org/graphql";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";
    private const string ThirdScene = "7b1e4d90-2c3a-4f81-95d6-0a8b7c6e5f43";

    // Words a reader could take for a number of scenes, plus the host's own aggregate phrasing.
    private static readonly string[] ForbiddenFragments =
        ["batch", "chunk", "unit", "slice", "succeeded"];

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    // One seeded scene carries two spellings of one source, so the stream answers it twice and a
    // distinct count in the database answers three where the run offers four.
    [Fact]
    public async Task TheDeclaredCountEqualsTheNumberOfTicksOverALibraryACountQueryWouldDisagreeWith()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await host.SeedStudioAsync(null, null);
        var twiceNamed = await host.SeedStudioSceneAsync(
            studioId, MonitorHost.StoredEndpoint, FirstScene);
        await host.AddSceneIdentityAsync(twiceNamed, StandardStashDbAddress, FirstScene);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, SecondScene);
        await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, ThirdScene);

        var instance = new Instance(_ => Accepted);
        var progress = new RecordingJobProgress();
        var ordered = new OrderedProgress(progress);

        var run = await SyncLibraryPlanner.RunAsync(
            SyncRegisters.Scenes,
            ct => host.LibraryScenes.SceneIdentities(WhisparrGeneration.V3, ct),
            identity => identity,
            instance.RegisterAsync,
            monitor: null,
            ordered,
            TestCt);

        Assert.Equal([4], progress.DeclaredUnitCounts);
        Assert.Equal(4, progress.Units.Count);
        Assert.Equal(4, run.Offered);
        Assert.Equal(3, instance.Offered.Distinct(StringComparer.Ordinal).Count());
    }

    // The host throws on a unit count declared after the first unit starts, which faults the job.
    [Fact]
    public async Task TheCountIsDeclaredBeforeTheFirstUnitStarts()
    {
        var (_, progress, ordered) = await RunOver([FirstScene, SecondScene], _ => Accepted);

        Assert.Equal(nameof(IJobProgress.DeclareUnitCount), ordered.Calls[0]);
        Assert.Single(progress.DeclaredUnitCounts);
    }

    // The host writes its own summary on the last unit's completion, so the run states its ending
    // through SetSummary after that completion rather than through a report at fraction one.
    [Fact]
    public async Task TheRunsLastProgressCallIsItsOwnSummaryAndItReportsNothing()
    {
        var (_, progress, ordered) = await RunOver([FirstScene, SecondScene], _ => Accepted);

        Assert.Equal(nameof(IJobProgress.SetSummary), ordered.Calls[^1]);
        Assert.Empty(progress.Reports);
        Assert.Single(progress.Summaries);
    }

    [Fact]
    public async Task NothingARunSaysCountsAnythingButScenesAndSites()
    {
        var (_, progress, _) = await RunOver(
            [FirstScene, SecondScene, ThirdScene],
            identity => identity == SecondScene ? AlreadyHeld : Accepted);

        var said = progress.Units
            .Select(reported => reported.Message)
            .Concat(progress.Summaries)
            .ToList();

        Assert.Equal(4, said.Count);
        foreach (var line in said)
        {
            Assert.NotNull(line);
            foreach (var forbidden in ForbiddenFragments)
            {
                Assert.DoesNotContain(forbidden, line, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Contains("Scene 3 of 3", said);
        Assert.Contains("scenes registered", progress.Summaries[0], StringComparison.Ordinal);

        var sites = new RecordingJobProgress();
        await SyncLibraryPlanner.RunAsync(
            SyncRegisters.Sites,
            ct => Streamed([FirstScene, SecondScene], ct),
            identity => identity,
            new Instance(_ => Accepted).RegisterAsync,
            (_, _, _) => Task.FromResult(new SceneMonitorTally(2, 1, 0, 0)),
            sites,
            TestCt);

        var overSites = sites.Units
            .Select(reported => reported.Message)
            .Concat(sites.Summaries)
            .ToList();

        Assert.Equal(3, overSites.Count);
        foreach (var line in overSites)
        {
            Assert.NotNull(line);
            foreach (var forbidden in ForbiddenFragments)
            {
                Assert.DoesNotContain(forbidden, line, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Contains("Site 2 of 2", overSites);
        Assert.Contains(
            "2 sites registered, 0 already in Whisparr, 0 refused, 4 scenes monitored, "
                + "2 scenes not monitored.",
            sites.Summaries[0],
            StringComparison.Ordinal);
    }

    // The host removes a completed unit's state only on disposal, so undisposed units leave one
    // entry per scene in a host dictionary.
    [Fact]
    public async Task EveryUnitIsDisposedAsWellAsCompleted()
    {
        var (_, progress, _) = await RunOver(
            [FirstScene, SecondScene, ThirdScene],
            identity => identity == ThirdScene ? Refused : Accepted);

        Assert.Equal(
            [
                new ReportedUnit(FirstScene, JobUnitOutcome.Succeeded, "Scene 1 of 3", Disposed: true),
                new ReportedUnit(SecondScene, JobUnitOutcome.Succeeded, "Scene 2 of 3", Disposed: true),
                new ReportedUnit(ThirdScene, JobUnitOutcome.Failed, "Scene 3 of 3", Disposed: true),
            ],
            progress.Units);
    }

    // Whether a scene is already held is the instance's own answer, so the second run offers every
    // scene again and each is skipped rather than failed.
    [Fact]
    public async Task ASecondRunOverTheSameLibraryRegistersNothingAndSkipsEveryScene()
    {
        var scenes = new[] { FirstScene, SecondScene, ThirdScene };

        var (first, _, _) = await RunOver(scenes, _ => Accepted);
        var (second, progress, _) = await RunOver(scenes, _ => AlreadyHeld);

        Assert.Equal(3, first.Registered);
        Assert.Equal(0, second.Registered);
        Assert.Equal(3, second.AlreadyHeld);
        Assert.Equal(0, second.Refused);
        Assert.All(progress.Units, unit => Assert.Equal(JobUnitOutcome.Skipped, unit.Outcome));
    }

    [Fact]
    public async Task ARefusalLeavesTheRestOfTheLibraryOfferedAndIsCounted()
    {
        var (run, _, _) = await RunOver(
            [FirstScene, SecondScene, ThirdScene],
            identity => identity == FirstScene ? Refused : Accepted);

        Assert.Equal(SyncLibraryRunOutcome.Completed, run.Outcome);
        Assert.Equal(3, run.Offered);
        Assert.Equal(1, run.Refused);
        Assert.Equal(2, run.Registered);
    }

    // A contained outbound failure answers nothing rather than throwing, and an answer nothing can
    // be read out of counts as refused rather than registered.
    [Fact]
    public async Task AnUnreachableInstanceLeavesEverySceneOfferedOnceAndAllOfThemRefused()
    {
        var (run, progress, _) = await RunOver([FirstScene, SecondScene, ThirdScene], _ => null);

        Assert.Equal(SyncLibraryRunOutcome.Completed, run.Outcome);
        Assert.Equal(3, run.Refused);
        Assert.Equal(0, run.Registered);
        Assert.Equal(3, progress.Units.Count);
        Assert.Single(progress.Summaries);
    }

    // The host stops a job by cancelling its token, so a cancellation is Cancelled and not Failed.
    [Fact]
    public async Task ACancellationIsCancelledWithWhatWasRegisteredBeforeItStillCounted()
    {
        using var stopping = new CancellationTokenSource();
        var instance = new Instance(_ => Accepted) { StopAfter = 2, Stopping = stopping };
        var progress = new RecordingJobProgress();
        var ordered = new OrderedProgress(progress);

        var run = await SyncLibraryPlanner.RunAsync(
            SyncRegisters.Scenes,
            ct => Streamed([FirstScene, SecondScene, ThirdScene], ct),
            identity => identity,
            instance.RegisterAsync,
            monitor: null,
            ordered,
            stopping.Token);

        Assert.Equal(SyncLibraryRunOutcome.Cancelled, run.Outcome);
        Assert.Equal(2, run.Registered);
        Assert.Equal(nameof(IJobProgress.SetSummary), ordered.Calls[^1]);
        Assert.Contains("then stopped", progress.Summaries[0], StringComparison.Ordinal);
    }

    // The host returns immediately from its progress refresh at a zero total, so a run that
    // declared zero would never be given an ending.
    [Fact]
    public async Task ALibraryCarryingNoIdentifierDeclaresNoCountAndStatesWhy()
    {
        var (run, progress, _) = await RunOver([], _ => Accepted);

        Assert.Equal(SyncLibraryRunOutcome.NothingToRegister, run.Outcome);
        Assert.Empty(progress.DeclaredUnitCounts);
        Assert.Empty(progress.Units);
        Assert.Equal(
            "No scene in the library carries an identifier this Whisparr names scenes by.",
            Assert.Single(progress.Summaries));
    }

    // Monitoring covers every scene in the library, not only the ones this run registered.
    [Fact]
    public async Task WithMonitoringOnASceneTheInstanceAlreadyHeldIsMonitoredToo()
    {
        var instance = new Instance(
            identity => identity == SecondScene ? AlreadyHeld : Accepted)
        {
            MonitorAnswers = _ => Monitored,
        };

        var run = await RunWith(instance, [FirstScene, SecondScene, ThirdScene]);

        Assert.Equal([FirstScene, SecondScene, ThirdScene], instance.MonitorAsked);
        Assert.Equal([201, 400, 201], instance.MonitorHandedStatus);
        Assert.Equal(3, run.Monitored);
        Assert.Equal(0, run.MonitorRefused);
    }

    // The instance is set up to answer, so an empty MonitorAsked separates monitoring being off
    // from monitoring being on and failing.
    [Fact]
    public async Task WithMonitoringOffNothingIsMonitored()
    {
        var instance = new Instance(_ => Accepted) { MonitorAnswers = _ => Monitored };
        var progress = new RecordingJobProgress();

        var run = await SyncLibraryPlanner.RunAsync(
            SyncRegisters.Scenes,
            ct => Streamed([FirstScene, SecondScene], ct),
            identity => identity,
            instance.RegisterAsync,
            monitor: null,
            progress,
            TestCt);

        Assert.Equal(0, run.Monitored);
        Assert.Empty(instance.MonitorAsked);
        Assert.DoesNotContain("monitored", progress.Summaries[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedSceneIsNotMonitoredAndAMonitorRefusalIsItsOwnCount()
    {
        var instance = new Instance(identity => identity == FirstScene ? Refused : Accepted)
        {
            MonitorAnswers = identity => identity == SecondScene ? null : Monitored,
        };

        var run = await RunWith(instance, [FirstScene, SecondScene, ThirdScene]);

        Assert.Equal([SecondScene, ThirdScene], instance.MonitorAsked);
        Assert.Equal(1, run.Monitored);
        Assert.Equal(1, run.MonitorRefused);
        Assert.Equal(1, run.Refused);
    }

    [Fact]
    public async Task TheSummaryNamesMonitoringOnlyWhereItWasAskedFor()
    {
        var asked = new Instance(_ => Accepted) { MonitorAnswers = _ => Monitored };
        var progress = new RecordingJobProgress();
        await SyncLibraryPlanner.RunAsync(
            SyncRegisters.Scenes,
            ct => Streamed([FirstScene], ct),
            identity => identity,
            asked.RegisterAsync,
            asked.MonitorAsync,
            progress,
            TestCt);

        var (_, without, _) = await RunOver([FirstScene], _ => Accepted);

        Assert.Contains("monitored", progress.Summaries[0], StringComparison.Ordinal);
        Assert.DoesNotContain("monitored", without.Summaries[0], StringComparison.Ordinal);
    }

    private static WhisparrResponse Accepted
        => RecordingWhisparrClient.Json(201, ProbeFixtures.Read(AcceptedFixture));

    private static WhisparrResponse AlreadyHeld
        => RecordingWhisparrClient.Json(400, ProbeFixtures.Read(AlreadyHeldFixture));

    private static WhisparrResponse Refused
        => RecordingWhisparrClient.Json(400, ProbeFixtures.Read(RefusedFixture));

    private static WhisparrResponse Monitored => RecordingWhisparrClient.Json(202, "{}");

    private static async Task<(SyncLibraryRun Run, RecordingJobProgress Progress, OrderedProgress Ordered)>
        RunOver(string[] identities, Func<string, WhisparrResponse?> answers)
    {
        var progress = new RecordingJobProgress();
        var ordered = new OrderedProgress(progress);

        var run = await SyncLibraryPlanner.RunAsync(
            SyncRegisters.Scenes,
            ct => Streamed(identities, ct),
            identity => identity,
            new Instance(answers).RegisterAsync,
            monitor: null,
            ordered,
            TestCt);

        return (run, progress, ordered);
    }

    private static Task<SyncLibraryRun> RunWith(Instance instance, string[] identities)
        => SyncLibraryPlanner.RunAsync(
            SyncRegisters.Scenes,
            ct => Streamed(identities, ct),
            identity => identity,
            instance.RegisterAsync,
            instance.MonitorAsync,
            new RecordingJobProgress(),
            TestCt);

    private static async IAsyncEnumerable<string> Streamed(
        IEnumerable<string> identities, [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var identity in identities)
        {
            ct.ThrowIfCancellationRequested();
            yield return identity;
        }

        await Task.CompletedTask;
    }

    private sealed class Instance(Func<string, WhisparrResponse?> answers)
    {
        public List<string> Offered { get; } = [];

        public List<string> MonitorAsked { get; } = [];

        // The status of the offer's answer each monitor call was handed, null where the offer
        // answered nothing. An accepted add names the instance's scene id in its answer; an
        // already-held one does not, so the caller reads it back.
        public List<int?> MonitorHandedStatus { get; } = [];

        public Func<string, WhisparrResponse?>? MonitorAnswers { get; init; }

        public int? StopAfter { get; init; }

        public CancellationTokenSource? Stopping { get; init; }

        public Task<SyncRegistration> RegisterAsync(string identity, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Offered.Add(identity);

            if (Offered.Count == StopAfter)
            {
                Stopping?.Cancel();
            }

            return Task.FromResult(SyncRegistration.Offered(answers(identity)));
        }

        // The monitor slot answers a tally because the site pass counts scenes under each site.
        // Here one entry is one scene, so the tally is always one scene.
        public Task<SceneMonitorTally> MonitorAsync(
            string identity, SyncRegistration offered, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            MonitorAsked.Add(identity);
            MonitorHandedStatus.Add(offered.Answer?.StatusCode);
            return Task.FromResult(SceneMonitorTally.For(MonitorAnswers?.Invoke(identity)));
        }
    }

    // The recorder keeps one list per member and cannot show call order across them, so the order
    // the host enforces silently is recorded here.
    internal sealed class OrderedProgress(RecordingJobProgress inner) : IJobProgress
    {
        public List<string> Calls { get; } = [];

        public void Report(double progress, string? subTask = null)
        {
            Calls.Add(nameof(Report));
            inner.Report(progress, subTask);
        }

        public void SetSummary(string summary)
        {
            Calls.Add(nameof(SetSummary));
            inner.SetSummary(summary);
        }

        public void DeclareUnitCount(int totalUnits)
        {
            Calls.Add(nameof(DeclareUnitCount));
            inner.DeclareUnitCount(totalUnits);
        }

        public IJobUnit StartUnit(string unitId, string? label = null)
        {
            Calls.Add(nameof(StartUnit));
            return inner.StartUnit(unitId, label);
        }
    }
}
