using System.Runtime.CompilerServices;
using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// The run that offers the whole library's scenes one at a time, and the three progress calls a
/// reader's experience of it depends on.
/// </summary>
/// <remarks>
/// The order of the progress calls is the subject as much as the counts are. The host refuses a unit
/// count declared once a unit has started, returns immediately from its progress refresh at a zero
/// total, and writes its own aggregate sentence over the job's summary on every unit completion. Each
/// of those is silent, so a run in the wrong order reports the wrong thing rather than failing.
/// </remarks>
public sealed class SyncLibraryPlannerTests
{
    private const string AcceptedFixture = "whisparr-v3-3.3.8.1097-scene-add-accepted.json";

    private const string AlreadyHeldFixture = "whisparr-v3-3.3.8.1097-scene-add-already-held.json";

    private const string RefusedFixture = "whisparr-v3-3.3.8.1097-scene-add-unknown-identifier.json";

    /// <summary>A spelling of the connected namespace that differs from the stored one.</summary>
    private const string StandardStashDbAddress = "https://stashdb.org/graphql";

    private const string FirstScene = "023bacff-8d1d-4f27-bac5-bdaf833f5616";
    private const string SecondScene = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";
    private const string ThirdScene = "7b1e4d90-2c3a-4f81-95d6-0a8b7c6e5f43";

    /// <summary>
    /// Words a reader could take for a number of scenes, and the host's own aggregate phrasing.
    /// </summary>
    /// <remarks>
    /// Listed here rather than in the source under test. A run reads correctly to a person only if
    /// every figure it states is a count of scenes, and the host's own sentence counts its units.
    /// </remarks>
    private static readonly string[] ForbiddenFragments =
        ["batch", "chunk", "unit", "slice", "succeeded"];

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// The declared count equals the number of ticks, over a library a count query would disagree
    /// about.
    /// </summary>
    /// <remarks>
    /// Read from a real relational library through the port the run itself walks. One scene carries
    /// two spellings of one source, which the host's rule treats as one source and the stream answers
    /// twice, so a distinct count taken in the database answers three where the run offers four. A
    /// declared total from a second cheaper query would leave the bar arriving early and the host
    /// writing its own summary at the wrong moment.
    /// </remarks>
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

    /// <summary>The count is declared before the first unit starts.</summary>
    /// <remarks>
    /// The host throws on a declaration made after that, which faults the whole job.
    /// </remarks>
    [Fact]
    public async Task TheCountIsDeclaredBeforeTheFirstUnitStarts()
    {
        var (_, progress, ordered) = await RunOver([FirstScene, SecondScene], _ => Accepted);

        Assert.Equal(nameof(IJobProgress.DeclareUnitCount), ordered.Calls[0]);
        Assert.Single(progress.DeclaredUnitCounts);
    }

    /// <summary>The run's last progress call is its own summary, and it reports nothing at all.</summary>
    /// <remarks>
    /// A report at fraction one is silently replaced: the host leaves the summary alone on a report,
    /// writes its own over it on the last unit's completion, and then copies the summary over the
    /// sub-task. So the run's ending has to be the summary member, after the final completion.
    /// </remarks>
    [Fact]
    public async Task TheRunsLastProgressCallIsItsOwnSummaryAndItReportsNothing()
    {
        var (_, progress, ordered) = await RunOver([FirstScene, SecondScene], _ => Accepted);

        Assert.Equal(nameof(IJobProgress.SetSummary), ordered.Calls[^1]);
        Assert.Empty(progress.Reports);
        Assert.Single(progress.Summaries);
    }

    /// <summary>Nothing a reader sees while the run works, or when it ends, counts anything else.</summary>
    [Fact]
    public async Task NothingARunSaysCountsAnythingButScenes()
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
    }

    /// <summary>Every unit is disposed as well as completed.</summary>
    /// <remarks>
    /// The host removes a completed unit's state only on disposal, so a run that completed every unit
    /// and disposed none would leave one entry per scene in a host dictionary - which on a library of
    /// millions is the whole cost this shape exists to avoid.
    /// </remarks>
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

    /// <summary>
    /// A second run over the same library registers nothing new and asks for no acquisition.
    /// </summary>
    /// <remarks>
    /// Whether the instance already holds a scene is its own answer rather than a comparison this
    /// product computes, so the second run offers every scene again and the instance declines every
    /// one as already added. The scene is skipped rather than failed: nothing about it changed.
    /// </remarks>
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

    /// <summary>A refusal does not end the run, and is counted apart from a scene already held.</summary>
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

    /// <summary>An instance nothing could be read from leaves every scene offered once, counted.</summary>
    /// <remarks>
    /// The contained outbound failure answers nothing rather than throwing, and an answer nothing can
    /// be read out of is refused rather than held: reporting a scene as registered that never left
    /// leaves a reader believing a catalogue is complete when it is empty.
    /// </remarks>
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

    /// <summary>
    /// A cancellation ends the run as cancelled, with what was registered before it still counted.
    /// </summary>
    /// <remarks>
    /// Cancelled rather than failed. Those scenes are in the instance's catalogue and there is
    /// nothing to undo, and the host stops a job by cancelling its token, so a failure here would
    /// report a shutdown as a fault.
    /// </remarks>
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

    /// <summary>
    /// A library carrying no identifier answers its own ending and declares no count at all.
    /// </summary>
    /// <remarks>
    /// The host returns immediately from its progress refresh at a zero total, so a run that declared
    /// zero would never derive a fraction and would never be given an ending.
    /// </remarks>
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

    /// <summary>
    /// With monitoring on, every scene the reader owns is monitored, including one the instance
    /// already held.
    /// </summary>
    /// <remarks>
    /// The choice means monitor what I own, not monitor what I just added. A run that monitored only
    /// its own registrations would leave the scenes a previous run registered unwatched, which is
    /// most of the library on any second press.
    /// </remarks>
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

    /// <summary>With monitoring off, nothing is monitored.</summary>
    /// <remarks>
    /// The instance would have answered. Nothing asked it, which is the difference between a choice
    /// that is off and one that is on and failing.
    /// </remarks>
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

    /// <summary>A refused scene is not monitored: there is no scene there to set a flag on.</summary>
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

    /// <summary>The summary names what was monitored only where monitoring was asked for.</summary>
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

    /// <summary>An instance answering each offer, recording what it was asked about in order.</summary>
    private sealed class Instance(Func<string, WhisparrResponse?> answers)
    {
        public List<string> Offered { get; } = [];

        public List<string> MonitorAsked { get; } = [];

        /// <summary>
        /// The status of the offer's own answer each monitor call was handed, or null where the offer
        /// answered nothing.
        /// </summary>
        /// <remarks>
        /// What resolves the instance's own scene id. An accepted add names it in its answer; an
        /// already-held one does not, so the caller has to read it back.
        /// </remarks>
        public List<int?> MonitorHandedStatus { get; } = [];

        public Func<string, WhisparrResponse?>? MonitorAnswers { get; init; }

        /// <summary>How many offers to take before the host stops the run.</summary>
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

        /// <summary>
        /// One scene marked wanted, answered as the tally one scene makes.
        /// </summary>
        /// <remarks>
        /// The run counts in scenes on both passes, so the monitor slot answers a tally rather than
        /// one response. On this pass an entry IS a scene, so the tally is always one scene.
        /// </remarks>
        public Task<SceneMonitorTally> MonitorAsync(
            string identity, SyncRegistration offered, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            MonitorAsked.Add(identity);
            MonitorHandedStatus.Add(offered.Answer?.StatusCode);
            return Task.FromResult(SceneMonitorTally.For(MonitorAnswers?.Invoke(identity)));
        }
    }

    /// <summary>
    /// The recording progress with one ordered log across all four of its members.
    /// </summary>
    /// <remarks>
    /// The recorder keeps a list per member, which cannot answer whether a declaration came before
    /// the first unit or whether the summary was the last thing said. Both of those are what the host
    /// enforces silently, so the order is recorded here and the counts are still read off the
    /// recorder underneath.
    /// </remarks>
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
