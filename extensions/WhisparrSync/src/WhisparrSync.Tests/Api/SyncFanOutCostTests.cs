using System.Globalization;
using Cove.Core.Interfaces;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Options;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// What one planned fan-out unit costs, driven through the REAL <see cref="IJobService.RunBatchAsync"/> —
/// a default interface method, so a minimal implementation inherits the batch loop under test rather than
/// restating it.
/// </summary>
/// <remarks>
/// <para>
/// The retained-bytes figure only means anything while the in-flight slot is genuinely HELD: the work
/// callback awaits a source the test completes only after sampling, so unit one occupies the semaphore and
/// every other unit's state machine is parked on it — the state the fan-out is in for a whole run. A
/// callback returning an already-completed task lets each unit finish before the next is created, so the
/// sample is of an empty queue and reports a small number that looks like an answer. The entered/returned
/// assertion before each sample is what separates the two.
/// </para>
/// <para>
/// The other half of a unit's cost is <c>RecalculateUnitProgress</c>, recorded here as arithmetic rather
/// than measured. It runs under the job service's lock on every new <c>StartUnit</c>, every
/// <c>IJobUnit.Report</c> and every <c>IJobUnit.Complete</c>, and each run makes four full enumerations of
/// the job's whole unit dictionary: three <c>Count()</c> passes (succeeded / failed / skipped) and one
/// <c>Sum()</c> over every unit's progress. So a run of N units that reports r times per unit costs
/// 4·N·(2+r) unit visits under a host-wide lock, quadratic in N. It is arithmetic and not a measurement
/// because the method is private to the host's job service, which this project neither references nor
/// should; and at any fixture size a single unit visit stays far below the per-unit outbound HTTP time, so
/// a timing here would be measuring the network. It becomes observable at fixture scale only where
/// registrations run back to back with no outbound call between them, which is the live pre-registration
/// burst rather than anything this class can see.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
public sealed class SyncFanOutCostTests(ITestOutputHelper output)
{
    private const int SmallUnitCount = 10_000;
    private const int LargeUnitCount = 100_000;

    private static IWhisparrAdapter Adapter(string version)
        => AdapterSelector.SelectForVersion(version, new WhisparrClient(new HttpClient()))!;

    private static WhisparrOptions Options(string version) => new() { SelectedVersion = version };

    private static CoveEntityRef Entity(int id, string stashId) => new(id, [stashId], []);

    private static CoveVideo Scene(int id, string? stashId)
        => new(id, $"Scene {id}", null, stashId is null ? [] : [stashId], [], [], []);

    private static IReadOnlyList<Ext.SyncUnit> SceneUnits(int count)
        => [.. Enumerable.Range(1, count).Select(i => Ext.SyncUnit.AddSlice(new Ext.SyncSlice(i - 1, i, i, i)))];

    // Mirrors what the job does between the count and the plan: take the id sitting on each boundary ordinal.
    private static IReadOnlyList<Ext.SyncSlice> SlicesFor(List<CoveVideo> videos)
    {
        var ordered = videos.OrderBy(v => v.CoveId).ToList();
        return Ext.PlanSceneSlices(
            ordered.Count, [.. Ext.SceneCutOrdinals(ordered.Count).Select(o => ordered[o - 1].CoveId)]);
    }

    // The same plan for a library too large to allocate. Only the NUMBER of boundary ids reaches the unit
    // count, so a strictly increasing stand-in for each one answers the count question exactly.
    private static IReadOnlyList<Ext.SyncSlice> SlicesForCount(int videoCount)
        => Ext.PlanSceneSlices(videoCount, [.. Ext.SceneCutOrdinals(videoCount).Select(o => o * 10)]);

    private static long SettledMemory()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        return GC.GetTotalMemory(forceFullCollection: true);
    }

    // Starts one batch, holds the single in-flight slot, and answers the bytes retained by the fan-out
    // alone: the unit list is allocated BEFORE the baseline, so the delta is the state machines and job-unit
    // records the batch adds on top of it.
    private static async Task<long> RetainedWhileParkedAsync(int unitCount)
    {
        var units = SceneUnits(unitCount);
        var progress = new RecordingJobProgress();
        IJobService jobs = new MinimalJobService();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = 0;
        var returned = 0;

        var baseline = SettledMemory();

        var batch = jobs.RunBatchAsync(
            units,
            maxInFlight: 1,
            async (_, jobUnit, _) =>
            {
                Interlocked.Increment(ref entered);
                await release.Task;
                Interlocked.Increment(ref returned);
                jobUnit.Complete(JobUnitOutcome.Succeeded);
            },
            progress,
            unitIdFactory: (_, index) => index.ToString(CultureInfo.InvariantCulture),
            labelFactory: _ => null);

        var occupied = SpinWait.SpinUntil(() => Volatile.Read(ref entered) == 1, TimeSpan.FromSeconds(30));

        // The guard the figure depends on: unit one is INSIDE the callback and has not left it, so it holds
        // the only in-flight slot and the remaining units are parked on the semaphore. Sampling without this
        // measures whatever happens to be alive, which for a non-blocking callback is nothing.
        Assert.True(
            occupied,
            "the in-flight slot was never held by exactly one unit: entered=" +
            Volatile.Read(ref entered).ToString(CultureInfo.InvariantCulture) + ", returned=" +
            Volatile.Read(ref returned).ToString(CultureInfo.InvariantCulture));
        Assert.Equal(1, Volatile.Read(ref entered));
        Assert.Equal(0, Volatile.Read(ref returned));

        var parked = SettledMemory();

        release.SetResult();
        var result = await batch;
        Assert.Equal(unitCount, result.TotalUnits);
        Assert.Equal(unitCount, Volatile.Read(ref returned));
        GC.KeepAlive(units);

        return parked - baseline;
    }

    [Fact]
    public async Task The_parked_fan_out_retains_bytes_in_proportion_to_the_planned_unit_count()
    {
        var small = await RetainedWhileParkedAsync(SmallUnitCount);
        var large = await RetainedWhileParkedAsync(LargeUnitCount);

        var slope = (double)(large - small) / (LargeUnitCount - SmallUnitCount);
        output.WriteLine(FormattableString.Invariant(
            $"parked fan-out retained: {SmallUnitCount} units -> {small} B, {LargeUnitCount} units -> {large} B"));
        output.WriteLine(FormattableString.Invariant($"slope: {slope:F1} bytes per parked unit"));

        // Only the machine-independent shape is asserted. An absolute byte figure varies with runtime, GC
        // mode and word size, so pinning one would be a flaky gate; the figure's home is the run output.
        Assert.True(slope > 0, $"expected a positive per-unit slope, got {slope:F1} B");
        Assert.True(
            large >= 5 * small,
            $"expected ten times the units to retain at least five times the bytes, got {large} B vs {small} B");
    }

    [Fact]
    public void The_planned_unit_count_is_entity_units_plus_the_slice_count_not_the_scene_count()
    {
        var studios = new[] { Entity(1, "s-1"), Entity(2, "s-2") };
        var performers = new[] { Entity(10, "p-1") };

        // Two studios + one performer, each planning a reflect-owned and a monitor unit.
        const int entityUnits = 6;
        const int eligible = 250;
        const int idLess = 7;
        var videos = new List<CoveVideo>();
        videos.AddRange(Enumerable.Range(100, eligible).Select(i => Scene(i, $"v-{i}")));
        videos.AddRange(Enumerable.Range(9000, idLess).Select(i => Scene(i, null)));

        var units = Ext.BuildSyncUnits(
            studios, performers, SlicesFor(videos), alsoMonitor: true, MonitorScope.AllScenes,
            Options("v3"), Adapter("v3"));

        // This library used to plan 256 units — one per eligible scene on top of the entity units. It now plans
        // the entity units plus ONE slice, and the 7 id-less scenes are inside that slice's range rather than
        // filtered out while planning.
        Assert.Equal(entityUnits + 1, units.Count);
        Assert.Equal(1, units.Count(u => u.Op == Ext.SyncOp.AddSlice));
    }

    [Fact]
    public void The_scene_unit_count_stops_growing_with_the_library()
    {
        var studios = new[] { Entity(1, "s-1") };
        const int entityUnits = 2;

        int UnitsFor(int videoCount) => Ext.BuildSyncUnits(
            studios, [], SlicesForCount(videoCount), alsoMonitor: true, MonitorScope.AllScenes,
            Options("v3"), Adapter("v3")).Count;

        var belowKnee = (UnitsFor(500), UnitsFor(5_000));
        var aboveKnee = (UnitsFor(1_000_000), UnitsFor(10_000_000));
        output.WriteLine(FormattableString.Invariant(
            $"units at 500/5000 scenes: {belowKnee.Item1}/{belowKnee.Item2}; at 1e6/1e7: {aboveKnee.Item1}/{aboveKnee.Item2}"));

        // Below the ceiling the count still varies with library size, and that is intended — the property is
        // that it stops varying in the unbounded direction. Stating both is what shows the flat result above is
        // not a rigged test.
        Assert.Equal(entityUnits + 1, belowKnee.Item1);
        Assert.Equal(entityUnits + 10, belowKnee.Item2);

        // A tenfold library plans the SAME number of units. These two used to be 1,000,002 and 10,000,002.
        Assert.Equal(aboveKnee.Item1, aboveKnee.Item2);
    }

    // Inherits RunBatchAsync — the thing under test — and supplies nothing else the batch loop touches.
    private sealed class MinimalJobService : IJobService
    {
        public string Enqueue(
            string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
            => throw new NotSupportedException();

        public bool Cancel(string jobId) => throw new NotSupportedException();

        public bool ReorderQueued(string jobId, string? beforeJobId) => throw new NotSupportedException();

        public JobInfo? GetJob(string jobId) => throw new NotSupportedException();

        public IReadOnlyList<JobInfo> GetAllJobs() => throw new NotSupportedException();

        public IReadOnlyList<JobInfo> GetJobHistory() => throw new NotSupportedException();
    }

    private sealed class RecordingJobProgress : IJobProgress
    {
        private readonly List<(string UnitId, string? Label)> _started = [];

        public IReadOnlyList<(string UnitId, string? Label)> Started
        {
            get
            {
                lock (_started)
                {
                    return [.. _started];
                }
            }
        }

        public void Report(double progress, string? subTask = null)
        {
        }

        public IJobUnit StartUnit(string unitId, string? label = null)
        {
            lock (_started)
            {
                _started.Add((unitId, label));
            }

            return new NullJobUnit(unitId, label);
        }
    }
}
