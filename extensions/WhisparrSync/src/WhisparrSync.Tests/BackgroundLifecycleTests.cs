using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;
using static Cove.Extensions.Shared.Testing.HttpResultUnwrap;

namespace WhisparrSync.Tests;

public sealed class BackgroundLifecycleTests
{
    private static readonly TimeSpan WorkerPeriod =
        TimeSpan.FromSeconds(WhisparrSyncOptions.BackstopIntervalFloorSeconds);

    private const int EveryWake = WhisparrSyncOptions.BackstopIntervalFloorSeconds;

    private const int EveryThirdWake = 3 * WhisparrSyncOptions.BackstopIntervalFloorSeconds;

    private static readonly TimeSpan DefaultInterval =
        TimeSpan.FromSeconds(WhisparrSyncOptions.DefaultBackstopIntervalSeconds);

    // Bounds a broken run rather than pacing a working one. Every wait below is for something the
    // worker does as soon as its continuations run.
    private static readonly TimeSpan SignalBudget = TimeSpan.FromSeconds(10);

    // Any instant; only the differences between instants are read.
    private static readonly DateTimeOffset Start = new(2026, 8, 31, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheWorkerKeepsRunningUntilItsTokenIsCancelled()
    {
        var clock = new ManualTimeProvider(Start);
        var pass = new BlockingPass(clock, blocking: false);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(clock, pass, WatchedSeeded(EveryWake));
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await clock.TimerCreatedAsync();

        // Two wakes served rather than a window of real time waited out. The second pass shows the
        // worker went back round the loop instead of returning after the first.
        clock.Advance(WorkerPeriod);
        await pass.ReturnedAsync(1);
        clock.Advance(WorkerPeriod);
        await pass.ReturnedAsync(2);

        Assert.False(worker.IsCompleted, "the worker returned without being asked to stop");
        Assert.NotNull(ProbeOf(extension).WorkerStartedAtUtc);
        Assert.Null(ProbeOf(extension).WorkerCancelledAtUtc);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    // The host's catch for a cancellation is conditioned on the token being cancelled, and it logs
    // everything else as a fault and does not restart. A worker that swallowed the cancellation and
    // returned normally would pass a check that only read the task as finished, so the status is
    // asserted rather than the completion.
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

    // The control for the three cases above: without it each of them could be reading a value that
    // is set from construction.
    [Fact]
    public void AnExtensionWhoseWorkerNeverRanReportsNeitherInstant()
    {
        var probe = ProbeOf(WhisparrSyncFixture.Create());

        Assert.Null(probe.WorkerStartedAtUtc);
        Assert.Null(probe.WorkerCancelledAtUtc);
    }

    // The pass does not return until it is released, so every wake driven past below arrives
    // mid-pass.
    [Fact]
    public async Task AWakeArrivingWhileAPassRunsStartsNoSecondPass()
    {
        var clock = new ManualTimeProvider(Start);
        var pass = new BlockingPass(clock);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(clock, pass, WatchedSeeded(EveryWake));
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await clock.TimerCreatedAsync();

        clock.Advance(WorkerPeriod);
        await pass.StartedAsync(1);

        // Three wakes while the pass is in flight. Nothing is waited for between them: the loop is
        // inside the pass, so it cannot receive one until the pass returns.
        for (var wake = 0; wake < 3; wake++)
        {
            clock.Advance(WorkerPeriod);
        }

        Assert.Equal(1, pass.Started);
        Assert.Equal(1, pass.MostInFlightAtOnce);

        pass.Release();
        await pass.ReturnedAsync(1);

        // The one wake the timer held is served after the pass returned, and never beside it.
        await pass.StartedAsync(2);
        Assert.Equal(1, pass.MostInFlightAtOnce);
        Assert.Equal([Start + WorkerPeriod, Start + (4 * WorkerPeriod)], pass.StartedAt);

        // The two wakes over the one the timer held were dropped rather than queued: the next pass
        // runs at the instant of the wake that follows, not at the instant a queued one would have.
        clock.Advance(WorkerPeriod);
        await pass.StartedAsync(3);
        Assert.Equal(Start + (5 * WorkerPeriod), pass.StartedAt[2]);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    // The control for the test above: a worker whose loop body never ran at all would report the
    // same "never more than one in flight".
    [Fact]
    public async Task EachWakeRunsItsOwnPassOnceTheOneBeforeItReturned()
    {
        var clock = new ManualTimeProvider(Start);
        var pass = new BlockingPass(clock, blocking: false);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(clock, pass, WatchedSeeded(EveryWake));
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await clock.TimerCreatedAsync();

        for (var wake = 1; wake <= 3; wake++)
        {
            clock.Advance(WorkerPeriod);
            await pass.ReturnedAsync(wake);
        }

        Assert.Equal(1, pass.MostInFlightAtOnce);
        Assert.Equal(
            [Start + WorkerPeriod, Start + (2 * WorkerPeriod), Start + (3 * WorkerPeriod)],
            pass.StartedAt);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    [Fact]
    public async Task AnIntervalLongerThanTheWakePeriodSkipsTheWakesBetween()
    {
        var clock = new ManualTimeProvider(Start);
        var pass = new BlockingPass(clock, blocking: false);
        var store = WatchedSeeded(EveryThirdWake);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(clock, pass, store);
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await clock.TimerCreatedAsync();

        for (var wake = 1; wake <= 4; wake++)
        {
            clock.Advance(WorkerPeriod);
            await store.ReadAsync(wake);
        }

        await pass.ReturnedAsync(2);

        // The first wake and the fourth. The two between them were inside the configured interval,
        // which the instant each pass ran at reports directly: a pass on a skipped wake would carry
        // that wake's own instant.
        Assert.Equal([Start + WorkerPeriod, Start + (4 * WorkerPeriod)], pass.StartedAt);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    // The floor is applied where the value is read rather than where it is saved, so a blob that
    // never passed through a save is floored too.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1)]
    public void AStoredIntervalBelowTheFloorIsReadAsTheFloor(int stored)
        => Assert.Equal(
            TimeSpan.FromSeconds(WhisparrSyncOptions.BackstopIntervalFloorSeconds),
            new WhisparrSyncOptions { BackstopIntervalSeconds = stored }.BackstopInterval);

    // The host does not restart a faulted worker, so a failure let out of the loop body would stop
    // the backstop until the extension is reloaded.
    [Fact]
    public async Task APassThatFailsUnexpectedlyLeavesTheWorkerRunning()
    {
        var clock = new ManualTimeProvider(Start);
        var pass = new BlockingPass(clock, blocking: false) { Throwing = true };
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(clock, pass, WatchedSeeded(EveryWake));
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await clock.TimerCreatedAsync();

        clock.Advance(WorkerPeriod);
        await pass.ReturnedAsync(1);
        clock.Advance(WorkerPeriod);
        await pass.ReturnedAsync(2);

        // The second pass ran at all, so the first failure was contained rather than let out.
        Assert.Equal(2, pass.Started);
        Assert.False(worker.IsCompleted, "a failed pass ended the worker");

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    // The follow-up opens a scope and resolves the library out of it, so a host whose scan service
    // this extension's container cannot produce fails the resolve rather than the enqueue.
    [Fact]
    public async Task AFollowUpThatFailsUnexpectedlyLeavesTheWorkerRunning()
    {
        var clock = new ManualTimeProvider(Start);
        var pass = new BlockingPass(clock, blocking: false);
        var noted = new RecordingLibrary(reached: true, ["/data"]);
        var followUp = new FollowUpScanCoalescer(clock, NullLogger.Instance);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(
            clock, pass, WatchedSeeded(EveryWake), followUp, library: null);
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await clock.TimerCreatedAsync();

        // The quiet period is below the wake period, so the first wake is the one the second advance
        // delivers, and the batch has already fallen quiet by then.
        followUp.NoteImported("/data/scene.mp4", noted);
        clock.Advance(FollowUpScanCoalescer.QuietPeriod);

        clock.Advance(WorkerPeriod);
        await pass.ReturnedAsync(1);
        clock.Advance(WorkerPeriod);
        await pass.ReturnedAsync(2);

        // The second wake's pass is the reading: a worker that ended on the first failure would leave
        // the first pass's own record behind and look like a success.
        Assert.Equal(2, pass.Started);
        Assert.False(worker.IsCompleted, "a failed follow-up ended the worker");

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    // The read is a live query and the store catches only a bind failure, so a transient database
    // failure reaches the loop.
    [Fact]
    public async Task AnIntervalReadThatFailsFallsBackToTheDefaultRatherThanEndingTheWorker()
    {
        var clock = new ManualTimeProvider(Start);
        var pass = new BlockingPass(clock, blocking: false);
        var store = new WatchedStore(
            new RaisingStore(() => new InvalidOperationException("the options blob could not be read")));
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(
            clock,
            pass,
            store,
            new FollowUpScanCoalescer(clock, NullLogger.Instance),
            new RecordingLibrary(reached: true, ["/data"]));
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await clock.TimerCreatedAsync();

        // The first wake passes whatever the interval reads: nothing has run yet.
        clock.Advance(WorkerPeriod);
        await pass.ReturnedAsync(1);

        // A wake the worker served and the gate held back. Awaited on its own read, so the advance
        // below is a second wake rather than one the timer collapsed into this one.
        clock.Advance(WorkerPeriod);
        await store.ReadAsync(2);

        clock.Advance(DefaultInterval);
        await pass.ReturnedAsync(2);

        // The second pass at the default interval's own cadence: a fallback that read as the floor
        // would have run one at the wake between, carrying that wake's instant.
        Assert.Equal(
            [Start + WorkerPeriod, Start + (2 * WorkerPeriod) + DefaultInterval],
            pass.StartedAt);
        Assert.False(worker.IsCompleted, "a failed interval read ended the worker");

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    // A containment that held on to a cancellation would turn a shutdown into a fault. The pass
    // count discriminates: a swallowed cancellation would let the loop body run on to it.
    [Fact]
    public async Task ACancellationArisingInsideAContainedCallStillEndsTheWorkerAsCancelled()
    {
        var clock = new ManualTimeProvider(Start);
        var pass = new BlockingPass(clock, blocking: false);
        var extension = WhisparrSyncFixture.Create();
        using var stop = new CancellationTokenSource();
        await using var services = WorkerServices(
            clock,
            pass,
            new RaisingStore(() =>
            {
                stop.Cancel();
                return new OperationCanceledException();
            }),
            new FollowUpScanCoalescer(clock, NullLogger.Instance),
            new RecordingLibrary(reached: true, ["/data"]));

        var worker = extension.RunAsync(services, stop.Token);
        await clock.TimerCreatedAsync();

        clock.Advance(WorkerPeriod);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);

        Assert.True(worker.IsCanceled, "the worker did not end as cancelled");
        Assert.False(worker.IsFaulted);
        Assert.Equal(0, pass.Started);
        Assert.NotNull(ProbeOf(extension).WorkerCancelledAtUtc);
    }

    // An outbound read that times out raises TaskCanceledException, which derives from
    // OperationCanceledException. The host's catch for a cancellation is conditioned on the token,
    // so a containment that rethrew this one would end the worker through no handler at all.
    // The token is left live on purpose: cancelling it first drives the shutdown path and passes
    // whether or not the containment tells the two apart.
    [Fact]
    public async Task ACancellationArisingWhileTheTokenIsLiveIsContainedRatherThanEndingTheWorker()
    {
        var clock = new ManualTimeProvider(Start);
        var pass = new BlockingPass(clock, blocking: false);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(
            clock,
            pass,
            new RaisingStore(() => new TaskCanceledException()),
            new FollowUpScanCoalescer(clock, NullLogger.Instance),
            new RecordingLibrary(reached: true, ["/data"]));
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await clock.TimerCreatedAsync();

        clock.Advance(WorkerPeriod);
        await pass.ReturnedAsync(1);

        Assert.False(worker.IsCompleted, "a cancellation the host never asked for ended the worker");

        // The cadence the default interval sets, which is what the contained read fell back to.
        clock.Advance(DefaultInterval);
        await pass.ReturnedAsync(2);
        Assert.Equal(
            [Start + WorkerPeriod, Start + WorkerPeriod + DefaultInterval], pass.StartedAt);

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    // The follow-up sits before the interval gate, so a live delivery is not left uncovered until
    // the next backstop interval comes round.
    [Fact]
    public async Task AQuietBatchIsScannedOnAWakeTheBackstopIntervalSkips()
    {
        var clock = new ManualTimeProvider(Start);
        var library = new RecordingLibrary(reached: true, ["/data"]);
        var followUp = new FollowUpScanCoalescer(clock, NullLogger.Instance);
        var store = WatchedSeeded(EveryThirdWake);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(
            clock, new BlockingPass(clock, blocking: false), store, followUp, library);
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await clock.TimerCreatedAsync();

        followUp.NoteImported("/data/scene.mp4", library);
        clock.Advance(FollowUpScanCoalescer.QuietPeriod);
        clock.Advance(WorkerPeriod);

        // The follow-up runs before the interval read and is awaited inline, so the wake's read is
        // the signal that the scan either happened or was skipped.
        await store.ReadAsync(1);

        Assert.Equal(["/data/scene.mp4"], Assert.Single(library.Scans));

        await stop.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker);
    }

    // A scan started after shutdown has begun reaches a host that is stopping. The files are on
    // disk and Cove's own library scan finds them, so dropping is recoverable where starting is not.
    [Fact]
    public async Task APendingBatchIsDroppedOnTheStopRatherThanScanned()
    {
        var clock = new ManualTimeProvider(Start);
        var library = new RecordingLibrary(reached: true, ["/data"]);
        var followUp = new FollowUpScanCoalescer(clock, NullLogger.Instance);
        var extension = WhisparrSyncFixture.Create();
        await using var services = WorkerServices(
            clock, new BlockingPass(clock, blocking: false), WatchedSeeded(EveryWake), followUp, library);
        using var stop = new CancellationTokenSource();

        var worker = extension.RunAsync(services, stop.Token);
        await clock.TimerCreatedAsync();

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

    private static ServiceProvider WorkerServices()
        => new ServiceCollection()
            .AddSingleton(TimeProvider.System)
            .AddSingleton(new FollowUpScanCoalescer(TimeProvider.System, NullLogger.Instance))
            .BuildServiceProvider();

    private static ServiceProvider WorkerServices(
        TimeProvider clock, IBackstopPass pass, IExtensionStore store)
        => WorkerServices(
            clock,
            pass,
            store,
            new FollowUpScanCoalescer(clock, NullLogger.Instance),
            new RecordingLibrary(reached: true, ["/data"]));

    // A null library is registered as no library at all, which is what the resolve inside the
    // follow-up meets on a host whose scan service this extension's container cannot produce.
    private static ServiceProvider WorkerServices(
        TimeProvider clock,
        IBackstopPass pass,
        IExtensionStore store,
        FollowUpScanCoalescer followUp,
        ICoveLibraryPort? library)
    {
        var options = new OptionsStore(store);
        var services = new ServiceCollection()
            .AddSingleton(clock)
            .AddSingleton(followUp)
            .AddScoped(_ => options)
            .AddScoped(_ => pass);

        if (library is not null)
        {
            services.AddScoped(_ => library);
        }

        return services.BuildServiceProvider();
    }

    private static WatchedStore WatchedSeeded(int intervalSeconds)
        => new(SeededStore(intervalSeconds));

    private static FakeStore SeededStore(int intervalSeconds)
    {
        var store = new FakeStore();
        new OptionsStore(store)
            .SaveAsync(
                new WhisparrSyncOptions { BackstopIntervalSeconds = intervalSeconds },
                TestContext.Current.CancellationToken)
            .GetAwaiter()
            .GetResult();

        return store;
    }

    private static HostConfigurationView ProbeOf(global::WhisparrSync.WhisparrSync extension)
        => ValueOf<HostConfigurationView>(
            extension.HostConfiguration(FakePrincipalAccessor.WithPermissions(Permissions.VideosRead)));

    private static T ValueOf<T>(IResult result)
        => Assert.IsType<T>(Assert.IsAssignableFrom<IValueHttpResult>(Unwrap(result)).Value);

    // The failure comes from a factory rather than as a value so that a test can cancel the
    // worker's own token as the read fails. That is the only way a cancellation arises inside the
    // read rather than beside it.
    private sealed class RaisingStore(Func<Exception> raised) : IExtensionStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default)
            => Task.FromException<string?>(raised());

        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteAsync(string key, CancellationToken ct = default) => Task.CompletedTask;

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => Task.FromException<Dictionary<string, string>>(raised());
    }

    // The signal for an occurrence that has not happened yet is made on demand, so a test may await
    // the third before the first has happened. An occurrence already recorded answers straight away
    // rather than waiting for the next.
    private sealed class Signals
    {
        private readonly Lock _gate = new();
        private readonly List<TaskCompletionSource> _signals = [];
        private int _count;

        public void Reach()
        {
            lock (_gate)
            {
                _count++;
                SignalFor(_count).TrySetResult();
            }
        }

        public Task Reached(int nth)
        {
            lock (_gate)
            {
                return SignalFor(nth).Task
                    .WaitAsync(SignalBudget, TestContext.Current.CancellationToken);
            }
        }

        private TaskCompletionSource SignalFor(int nth)
        {
            while (_signals.Count < nth)
            {
                _signals.Add(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            }

            return _signals[nth - 1];
        }
    }

    // The signal is raised as the read begins rather than when it answers, so a store whose reads
    // raise is watchable the same way. The follow-up step runs before the read and is awaited
    // inline, so a signalled read is also a finished follow-up.
    private sealed class WatchedStore(IExtensionStore inner) : IExtensionStore
    {
        private readonly Signals _reads = new();

        public Task ReadAsync(int nth) => _reads.Reached(nth);

        public Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            _reads.Reach();
            return inner.GetAsync(key, ct);
        }

        public Task SetAsync(string key, string value, CancellationToken ct = default)
            => inner.SetAsync(key, value, ct);

        public Task DeleteAsync(string key, CancellationToken ct = default)
            => inner.DeleteAsync(key, ct);

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => inner.GetAllAsync(ct);
    }

    // It records the highest number of passes in flight at any one instant rather than a total. A
    // second pass that began and ended between two readings would leave a total correct and the
    // property it stands for broken.
    private sealed class BlockingPass(TimeProvider clock, bool blocking = true) : IBackstopPass
    {
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Signals _starts = new();
        private readonly Signals _returns = new();
        private readonly List<DateTimeOffset> _startedAt = [];
        private int _inFlight;

        public bool Throwing { get; init; }

        public int Started => _startedAt.Count;

        public List<DateTimeOffset> StartedAt => _startedAt;

        public int MostInFlightAtOnce { get; private set; }

        public async Task<BackstopPassResult> RunAsync(CancellationToken ct)
        {
            _startedAt.Add(clock.GetUtcNow());
            _inFlight++;
            MostInFlightAtOnce = Math.Max(MostInFlightAtOnce, _inFlight);
            _starts.Reach();

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

                return new BackstopPassResult(BackstopPassOutcome.Walked, null, 0, 0, 0, 0, 0);
            }
            finally
            {
                _inFlight--;
                _returns.Reach();
            }
        }

        public Task StartedAsync(int nth) => _starts.Reached(nth);

        public Task ReturnedAsync(int nth) => _returns.Reached(nth);

        public void Release() => _released.TrySetResult();
    }

    // The worker's wake period is far longer than a test may wait for, and a pass gated on a
    // configured interval cannot be driven at all without a clock a test moves by hand.
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly Signals _created = new();
        private DateTimeOffset _now = start;

        // A clock advanced before the timer exists moves the instant the timer is scheduled from,
        // so the wake is never delivered and the test waits on a pass that cannot run.
        public Task TimerCreatedAsync() => _created.Reached(1);

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

            _created.Reach();
            return timer;
        }

        // The callbacks run outside the lock: one of them may schedule a timer of its own.
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
