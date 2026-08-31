using Cove.Core.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests;

/// <summary>
/// The half of the worker's lifecycle that needs no host: that it keeps running until its token is
/// cancelled, that cancelling it ends the task, that the ending classifies as cancelled, and that its
/// passes never overlap.
/// </summary>
/// <remarks>
/// The host stops the worker by cancelling the token and then blocking on the returned task, so a
/// worker that ignored the token would hang shutdown, disable and rebuild instead of failing. The
/// case that a returned-immediately worker could not pass is asserted first.
/// <para>
/// The instants are read through the host-configuration projection rather than off a field, so what
/// is asserted here is the same reading the containerized suite takes.
/// </para>
/// </remarks>
public sealed class BackgroundLifecycleTests
{
    /// <summary>How long the worker is left running before it is asked to be still running.</summary>
    /// <remarks>
    /// Far below the worker's own wake period, so a pass means it is waiting rather than that the
    /// window was too short to catch a wake.
    /// </remarks>
    private static readonly TimeSpan StillRunningWindow = TimeSpan.FromMilliseconds(250);

    /// <summary>The worker's own wake period, transcribed by hand from the floor it is built on.</summary>
    private static readonly TimeSpan WorkerPeriod =
        TimeSpan.FromSeconds(WhisparrSyncOptions.BackstopIntervalFloorSeconds);

    /// <summary>A configured interval no longer than a wake, so every wake is due.</summary>
    private const int EveryWake = WhisparrSyncOptions.BackstopIntervalFloorSeconds;

    /// <summary>A configured interval three wakes long.</summary>
    private const int EveryThirdWake = 3 * WhisparrSyncOptions.BackstopIntervalFloorSeconds;

    /// <summary>Real time allowed for the worker's continuations between two clock advances.</summary>
    private static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(100);

    /// <summary>Where the driveable clock starts. Any instant; only the differences are read.</summary>
    private static readonly DateTimeOffset Start = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheWorkerKeepsRunningUntilItsTokenIsCancelled()
    {
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices();
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await Task.Delay(StillRunningWindow, TestContext.Current.CancellationToken);

        Assert.False(worker.IsCompleted, "the worker returned without being asked to stop");
        Assert.NotNull(ProbeOf(extension).WorkerStartedAtUtc);
        Assert.Null(ProbeOf(extension).WorkerCancelledAtUtc);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    /// <summary>
    /// A cancelled worker ends as cancelled rather than as faulted.
    /// </summary>
    /// <remarks>
    /// The distinction is the host's: its catch for the cancellation is conditioned on the token being
    /// cancelled, and everything else it logs as a fault and does not restart. A worker that swallowed
    /// the cancellation and returned normally would pass an assertion that only read the task as
    /// finished, so the status is asserted rather than the completion.
    /// </remarks>
    [Fact]
    public async Task ACancelledWorkerEndsAsCancelledRatherThanFaulted()
    {
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices();
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);

        Assert.True(worker.IsCanceled, "the worker did not end as cancelled");
        Assert.False(worker.IsFaulted);
    }

    /// <summary>
    /// The cancellation is recorded, at or after the start, and the start reading survives it.
    /// </summary>
    /// <remarks>
    /// Both instants together are what tells a stopped worker from one that never ran: a probe
    /// carrying neither is a worker the host never started at all.
    /// </remarks>
    [Fact]
    public async Task BothHalvesOfTheLifecycleAreReadableAfterTheStop()
    {
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices();
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);

        var probe = ProbeOf(extension);
        Assert.NotNull(probe.WorkerStartedAtUtc);
        Assert.NotNull(probe.WorkerCancelledAtUtc);
        Assert.True(
            probe.WorkerCancelledAtUtc >= probe.WorkerStartedAtUtc,
            $"the worker was recorded as cancelled at {probe.WorkerCancelledAtUtc:O}, before it started at {probe.WorkerStartedAtUtc:O}");
    }

    /// <summary>A worker that never ran reports neither instant.</summary>
    /// <remarks>
    /// The discriminating control for the three above: without it each of them could equally be
    /// reading a value that is set from construction.
    /// </remarks>
    [Fact]
    public void AnExtensionWhoseWorkerNeverRanReportsNeitherInstant()
    {
        var probe = ProbeOf(WhisparrSyncFixture.Create());

        Assert.Null(probe.WorkerStartedAtUtc);
        Assert.Null(probe.WorkerCancelledAtUtc);
    }

    /// <summary>
    /// A wake arriving while a pass is running starts no second pass.
    /// </summary>
    /// <remarks>
    /// The pass under test does not return until it is released, so every wake the clock is driven
    /// past below arrives mid-pass. The reading is the highest number ever in flight at one instant,
    /// not the total: a second pass that started and finished between two samples would leave the
    /// total right and the property broken.
    /// </remarks>
    [Fact]
    public async Task AWakeArrivingWhileAPassRunsStartsNoSecondPass()
    {
        var pass = new BlockingPass();
        var clock = new ManualTimeProvider(Start);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(clock, pass, EveryWake);
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await Settle();

        clock.Advance(WorkerPeriod);
        await pass.StartedAsync();

        for (var wake = 0; wake < 3; wake++)
        {
            clock.Advance(WorkerPeriod);
            await Settle();
        }

        Assert.Equal(1, pass.Started);
        Assert.Equal(1, pass.MostInFlightAtOnce);

        pass.Release();
        await Settle();

        // The wake that arrived mid-pass was skipped rather than queued: releasing the pass does not
        // set off the ones that were dropped.
        Assert.Equal(1, pass.MostInFlightAtOnce);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    /// <summary>Wake after wake produces pass after pass, one at a time.</summary>
    /// <remarks>
    /// The discriminating control for the test above: without it, a worker whose loop body never ran
    /// at all would report the same "never more than one in flight".
    /// </remarks>
    [Fact]
    public async Task EachWakeRunsItsOwnPassOnceTheOneBeforeItReturned()
    {
        var pass = new BlockingPass(blocking: false);
        var clock = new ManualTimeProvider(Start);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(clock, pass, EveryWake);
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await Settle();

        for (var wake = 0; wake < 3; wake++)
        {
            clock.Advance(WorkerPeriod);
            await Settle();
        }

        Assert.Equal(3, pass.Started);
        Assert.Equal(1, pass.MostInFlightAtOnce);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    /// <summary>An interval longer than the wake period skips the wakes in between.</summary>
    [Fact]
    public async Task AnIntervalLongerThanTheWakePeriodSkipsTheWakesBetween()
    {
        var pass = new BlockingPass(blocking: false);
        var clock = new ManualTimeProvider(Start);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(clock, pass, EveryThirdWake);
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await Settle();

        for (var wake = 0; wake < 4; wake++)
        {
            clock.Advance(WorkerPeriod);
            await Settle();
        }

        // The first wake and the fourth. The two between them were inside the configured interval.
        Assert.Equal(2, pass.Started);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    /// <summary>A stored interval below the floor is honoured as the floor.</summary>
    /// <remarks>
    /// Applied where the value is read rather than where it is saved, so a blob that never passed
    /// through a save is floored too.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void AStoredIntervalBelowTheFloorIsReadAsTheFloor(int stored)
        => Assert.Equal(
            TimeSpan.FromSeconds(WhisparrSyncOptions.BackstopIntervalFloorSeconds),
            new WhisparrSyncOptions { BackstopIntervalSeconds = stored }.BackstopInterval);

    /// <summary>A pass that fails unexpectedly does not take the worker with it.</summary>
    /// <remarks>
    /// The host treats anything but a cancellation as a fault and does not restart the worker, so a
    /// failure let out of the loop body would stop the backstop until the extension is reloaded.
    /// </remarks>
    [Fact]
    public async Task APassThatFailsUnexpectedlyLeavesTheWorkerRunning()
    {
        var pass = new BlockingPass(blocking: false) { Throwing = true };
        var clock = new ManualTimeProvider(Start);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(clock, pass, EveryWake);
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await Settle();

        clock.Advance(WorkerPeriod);
        await Settle();
        clock.Advance(WorkerPeriod);
        await Settle();

        Assert.Equal(2, pass.Started);
        Assert.False(worker.IsCompleted, "a failed pass ended the worker");

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    /// <summary>A quiet batch is covered by a wake, whether or not that wake runs a pass.</summary>
    /// <remarks>
    /// The follow-up sits before the interval gate, so a live delivery is not left uncovered until
    /// the next backstop interval comes round.
    /// </remarks>
    [Fact]
    public async Task AQuietBatchIsScannedOnAWakeTheBackstopIntervalSkips()
    {
        var clock = new ManualTimeProvider(Start);
        var library = new RecordingLibrary(reached: true, ["/data"]);
        var followUp = new FollowUpScanCoalescer(clock, NullLogger.Instance);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(
            clock, new BlockingPass(blocking: false), EveryThirdWake, followUp, library);
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await Settle();

        followUp.NoteImported("/data/scene.mp4", library);
        clock.Advance(FollowUpScanCoalescer.QuietPeriod);
        clock.Advance(WorkerPeriod);
        await Settle();

        Assert.Equal(["/data/scene.mp4"], Assert.Single(library.Scans));

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    /// <summary>
    /// A batch still pending when the worker is stopped is dropped, and the stop still classifies as
    /// cancelled.
    /// </summary>
    /// <remarks>
    /// A scan started after shutdown has begun reaches a host that is stopping. The files are on disk
    /// and Cove's own library scan finds them, so dropping is recoverable where starting is not.
    /// </remarks>
    [Fact]
    public async Task APendingBatchIsDroppedOnTheStopRatherThanScanned()
    {
        var clock = new ManualTimeProvider(Start);
        var library = new RecordingLibrary(reached: true, ["/data"]);
        var followUp = new FollowUpScanCoalescer(clock, NullLogger.Instance);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(
            clock, new BlockingPass(blocking: false), EveryWake, followUp, library);
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await Settle();

        // Noted with no wake between it and the stop, so the batch is still pending when the token
        // is cancelled.
        followUp.NoteImported("/data/scene.mp4", library);
        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);

        Assert.Empty(library.Scans);
        Assert.True(worker.IsCanceled, "the worker did not end as cancelled");

        // Dropped rather than left pending: a flush afterwards finds nothing.
        followUp.Flush(library);
        Assert.Empty(library.Scans);
    }

    /// <summary>The services the worker resolves, registered as the extension registers them.</summary>
    private static ServiceProvider WorkerServices()
        => new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(new FollowUpScanCoalescer(TimeProvider.System, NullLogger.Instance))
            .BuildServiceProvider();

    /// <summary>The worker's services, with a driveable clock and a pass a test can watch.</summary>
    private static ServiceProvider WorkerServices(
        TimeProvider clock, IBackstopPass pass, int intervalSeconds)
        => WorkerServices(
            clock,
            pass,
            intervalSeconds,
            new FollowUpScanCoalescer(clock, NullLogger.Instance),
            new RecordingLibrary(reached: true, ["/data"]));

    /// <inheritdoc cref="WorkerServices(TimeProvider, IBackstopPass, int)"/>
    private static ServiceProvider WorkerServices(
        TimeProvider clock,
        IBackstopPass pass,
        int intervalSeconds,
        FollowUpScanCoalescer followUp,
        ICoveLibraryPort library)
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);
        options
            .SaveAsync(
                new WhisparrSyncOptions { BackstopIntervalSeconds = intervalSeconds },
                TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();

        return new ServiceCollection()
            .AddSingleton(clock)
            .AddSingleton(followUp)
            .AddScoped(_ => options)
            .AddScoped(_ => pass)
            .AddScoped(_ => library)
            .BuildServiceProvider();
    }

    /// <summary>Lets the worker's own continuations run before the next reading is taken.</summary>
    /// <remarks>
    /// The clock is driven from this thread while the loop awaits on another, so a reading taken
    /// straight after an advance would be taken before the wake had been received.
    /// </remarks>
    private static Task Settle() => Task.Delay(SettleWindow, TestContext.Current.CancellationToken);

    private static HostConfigurationView ProbeOf(global::WhisparrSync.WhisparrSync extension)
        => ValueOf<HostConfigurationView>(
            extension.HostConfiguration(FakePrincipalAccessor.WithPermissions(Permissions.VideosRead)));

    private static T ValueOf<T>(IResult result)
        => Assert.IsType<T>(Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

    /// <summary>
    /// A pass a test starts, watches and releases.
    /// </summary>
    /// <remarks>
    /// It records the highest number of passes in flight at any one instant rather than a total. A
    /// second pass that began and ended between two readings would leave a total correct and the
    /// property it stands for broken.
    /// </remarks>
    private sealed class BlockingPass(bool blocking = true) : IBackstopPass
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;

        /// <summary>Whether the pass ends by throwing.</summary>
        public bool Throwing { get; init; }

        /// <summary>How many passes have begun.</summary>
        public int Started { get; private set; }

        /// <summary>The most that were ever running at one instant.</summary>
        public int MostInFlightAtOnce { get; private set; }

        public async Task<BackstopPassResult> RunAsync(CancellationToken ct)
        {
            Started++;
            _inFlight++;
            MostInFlightAtOnce = Math.Max(MostInFlightAtOnce, _inFlight);
            _entered.TrySetResult();

            try
            {
                if (blocking)
                {
                    await _released.Task.WaitAsync(ct).ConfigureAwait(false);
                }

                if (Throwing)
                {
                    throw new InvalidOperationException("the pass failed in a way it does not classify");
                }

                return new BackstopPassResult(BackstopPassOutcome.Walked, null, 0, 0, 0, 0);
            }
            finally
            {
                _inFlight--;
            }
        }

        /// <summary>Returns once a pass has begun.</summary>
        public Task StartedAsync() => _entered.Task.WaitAsync(TestContext.Current.CancellationToken);

        /// <summary>Lets the pass in flight return.</summary>
        public void Release() => _released.TrySetResult();
    }

    /// <summary>
    /// A clock a test moves by hand, firing the timers whose due instant it passes.
    /// </summary>
    /// <remarks>
    /// The worker's wake period is far longer than a test may wait for, and a pass gated on a
    /// configured interval cannot be driven at all without one.
    /// </remarks>
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, this);
            lock (_gate)
            {
                _timers.Add(timer);
                timer.Reschedule(_now, dueTime, period);
            }

            return timer;
        }

        /// <summary>Moves the clock on by <paramref name="by"/>, firing whatever falls due.</summary>
        /// <remarks>
        /// The callbacks run outside the lock: one of them may schedule a timer of its own.
        /// </remarks>
        public void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(timer => timer.IsDue(_now))];
                foreach (var timer in due)
                {
                    timer.Fired(_now);
                }
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        internal void Forget(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        internal DateTimeOffset Now
        {
            get
            {
                lock (_gate)
                {
                    return _now;
                }
            }
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, ManualTimeProvider clock)
        : ITimer
    {
        private DateTimeOffset _due = DateTimeOffset.MaxValue;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Reschedule(clock.Now, dueTime, period);
            return true;
        }

        public void Dispose() => clock.Forget(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public void Fire() => callback(state);

        internal bool IsDue(DateTimeOffset now) => now >= _due;

        internal void Fired(DateTimeOffset now)
            => _due = _period <= TimeSpan.Zero ? DateTimeOffset.MaxValue : now + _period;

        internal void Reschedule(DateTimeOffset now, TimeSpan dueTime, TimeSpan period)
        {
            _period = period;
            _due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + dueTime;
        }
    }
}
