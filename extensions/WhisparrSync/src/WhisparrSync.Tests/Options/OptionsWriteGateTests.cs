using System.Collections.Concurrent;
using System.Reflection;
using Cove.Plugins;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

// The interleave is constructed rather than waited for. The store's load parks on a signal this
// file releases, so the moment one writer has loaded and not yet saved is one the test chooses.
public sealed class OptionsWriteGateTests
{
    private const string MovedHost = "http://cove.example:8080";
    private const string StoredAddress = "http://whisparr.example:6969";
    private const string StoredWatermark = "2026-01-01T00:00:00+00:00";

    // The interval is a string where the model declares an int, which is what fails the bind. The
    // address and the watermark are the members a save built on the fallback would replace.
    private const string UnbindableBlob = $$"""
        {"v3":{"address":"{{StoredAddress}}","backstopWatermarkUtc":"{{StoredWatermark}}"},"backstopIntervalSeconds":"every so often"}
        """;

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    // How long a second writer is given to reach the store, before it is taken to be parked
    // somewhere the store cannot see.
    private static readonly TimeSpan Lapse = TimeSpan.FromMilliseconds(500);

    private static readonly DateTimeOffset WorkedAt = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

    // The final blob is the subject, not a call count. A gate that serialised the saves and still
    // let the second one carry a value read before the first would satisfy any count.
    [Fact]
    public async Task TwoMutationsThatMeetBothSurviveInTheStoredBlob()
    {
        var store = new ParkingStore();
        var options = new OptionsStore(store);
        using var gate = new OptionsWriteGate();

        var host = gate.MutateAsync(options, stored => stored with { CallbackHost = MovedHost }, TestCt);
        await store.LoadBegun;
        var behavior = gate.MutateAsync(
            options, stored => stored with { UpgradeBehavior = UpgradeBehavior.Replace }, TestCt);

        // Both writers are now as far as they can get: one inside the gate at its load, the other
        // either waiting for the gate or, with no gate, at a load of its own.
        await store.LoadsBegunOrLapse(2, Lapse);
        store.ReleaseLoads();
        await Task.WhenAll(host, behavior).WaitAsync(Budget, TestCt);

        var persisted = await options.LoadAsync(TestCt);
        Assert.Equal(MovedHost, persisted.CallbackHost);
        Assert.Equal(UpgradeBehavior.Replace, persisted.UpgradeBehavior);
        Assert.Equal(2, store.Saves);
    }

    [Fact]
    public async Task AFoldThatChangesNothingWritesNothing()
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);
        await options.SaveAsync(new WhisparrSyncOptions { CallbackHost = MovedHost }, TestCt);
        var writes = store.SetCallCount;
        using var gate = new OptionsWriteGate();

        var answered = await gate.MutateAsync(
            options, stored => stored with { CallbackHost = MovedHost }, TestCt);

        Assert.Equal(writes, store.SetCallCount);
        Assert.Equal(MovedHost, answered.CallbackHost);
    }

    [Fact]
    public async Task AFoldThatThrowsLeavesTheGateOpenForTheNextMutation()
    {
        var options = new OptionsStore(new FakeStore());
        using var gate = new OptionsWriteGate();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.MutateAsync(
                options,
                _ => throw new InvalidOperationException("the fold refused"),
                TestCt));
        Assert.Equal("the fold refused", refused.Message);

        var answered = await gate
            .MutateAsync(options, stored => stored with { CallbackHost = MovedHost }, TestCt)
            .WaitAsync(Budget, TestCt);

        Assert.Equal(MovedHost, answered.CallbackHost);
    }

    // The store here does not honour cancellation, so the wait for the gate is the only thing in
    // the call that can answer a cancelled token.
    [Fact]
    public async Task ACancelledWaitIsCancelledAndTakesNothingWithIt()
    {
        var store = new ParkingStore();
        var options = new OptionsStore(store);
        using var gate = new OptionsWriteGate();

        var held = gate.MutateAsync(options, stored => stored with { CallbackHost = MovedHost }, TestCt);
        await store.LoadBegun;

        using var cancelling = new CancellationTokenSource();
        var abandoned = gate.MutateAsync(
            options, stored => stored with { UpgradeBehavior = UpgradeBehavior.Replace }, cancelling.Token);
        await cancelling.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => abandoned.WaitAsync(Budget, TestCt));

        store.ReleaseLoads();
        await held.WaitAsync(Budget, TestCt);

        var after = await gate
            .MutateAsync(options, stored => stored with { DefaultMonitorScope = MonitorScope.AllScenes }, TestCt)
            .WaitAsync(Budget, TestCt);

        Assert.Equal(MonitorScope.AllScenes, after.DefaultMonitorScope);
        Assert.Equal(MovedHost, after.CallbackHost);
        Assert.Equal(UpgradeBehavior.Add, after.UpgradeBehavior);
    }

    // A fold that cannot await keeps an outbound request and a host import outside the lock.
    [Fact]
    public void TheGateOffersOneMutationAndItTakesASynchronousFold()
    {
        var mutate = Assert.Single(
            typeof(OptionsWriteGate)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly),
            method => method.Name.EndsWith("Async", StringComparison.Ordinal));

        Assert.Equal(nameof(OptionsWriteGate.MutateAsync), mutate.Name);
        var fold = Assert.Single(mutate.GetParameters(), parameter => parameter.Name == "fold");
        Assert.Equal(typeof(Func<WhisparrSyncOptions, WhisparrSyncOptions>), fold.ParameterType);
    }

    // The fold writes an instant that always differs, so the equal-value short circuit never stands
    // in for the refusal.
    [Fact]
    public async Task AMutationOnABlobTheModelCannotBindIsNotSavedOverIt()
    {
        var store = new FakeStore();
        await store.SetAsync(OptionsStore.Key, UnbindableBlob, TestCt);
        var options = new OptionsStore(store);
        var log = new CountingLogger();
        using var gate = new OptionsWriteGate(log);
        var writes = store.SetCallCount;

        var answered = await gate.MutateAsync(options, RecordAnImport, TestCt);

        var persisted = await store.GetAsync(OptionsStore.Key, TestCt);
        Assert.Contains(StoredAddress, persisted);
        Assert.Contains(StoredWatermark, persisted);
        Assert.Equal(UnbindableBlob, persisted);
        Assert.Equal(writes, store.SetCallCount);
        Assert.Null(answered.ImportHealth.LastWorkedAtUtc);
        Assert.Equal(1, log.Refusals);
    }

    // An absent blob and an unbindable one are different states. Without this, a gate that refused
    // every write would satisfy the refusal above.
    [Fact]
    public async Task AnEmptyStoreLoadsDefaultsAndTheSameMutationIsSaved()
    {
        var store = new FakeStore();
        var options = new OptionsStore(store);
        var log = new CountingLogger();
        using var gate = new OptionsWriteGate(log);

        var answered = await gate.MutateAsync(options, RecordAnImport, TestCt);

        Assert.Equal(WorkedAt, answered.ImportHealth.LastWorkedAtUtc);
        Assert.Equal(1, store.SetCallCount);
        var reloaded = await options.LoadAsync(TestCt);
        Assert.Equal(WorkedAt, reloaded.ImportHealth.LastWorkedAtUtc);
        Assert.Equal(0, log.Refusals);
    }

    // The fold the import channel runs on every successful import.
    private static WhisparrSyncOptions RecordAnImport(WhisparrSyncOptions stored)
        => stored with { ImportHealth = stored.ImportHealth with { LastWorkedAtUtc = WorkedAt } };

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private sealed class CountingLogger : ILogger
    {
        private const int RefusedMutationEventId = 2116;

        public int Refusals { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == RefusedMutationEventId)
            {
                Refusals++;
            }
        }
    }

    // A store whose load parks until the test releases it. Cancellation is deliberately not
    // honoured, so a cancelled call can only be answered by the gate's own wait.
    private sealed class ParkingStore : IExtensionStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _firstLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _loads;
        private int _saves;

        public Task LoadBegun => _firstLoad.Task;

        public int Saves => Volatile.Read(ref _saves);

        public void ReleaseLoads() => _released.TrySetResult();

        public async Task LoadsBegunOrLapse(int count, TimeSpan lapse)
        {
            var step = TimeSpan.FromMilliseconds(10);
            for (var waited = TimeSpan.Zero; waited < lapse && Volatile.Read(ref _loads) < count; waited += step)
            {
                await Task.Delay(step).ConfigureAwait(false);
            }
        }

        public async Task<string?> GetAsync(string key, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _loads);
            _firstLoad.TrySetResult();
            await _released.Task.ConfigureAwait(false);
            return _values.GetValueOrDefault(key);
        }

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _saves);
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            _values.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, string>(_values, StringComparer.Ordinal));
    }
}
