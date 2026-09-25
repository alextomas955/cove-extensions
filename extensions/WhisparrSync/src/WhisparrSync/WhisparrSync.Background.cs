using Cove.Extensions.Shared;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Import;
using WhisparrSync.Options;

namespace WhisparrSync;

// IBackgroundExtension is declared here rather than inherited: unlike the data and stateful
// capabilities, FullExtensionBase does not implement it.
public sealed partial class WhisparrSync : IBackgroundExtension
{
    // A pass cannot run more often than a wake, so the wake period is also the backstop interval's
    // floor, and the two are one value rather than two that can drift apart.
    private static readonly TimeSpan WorkerPeriod =
        TimeSpan.FromSeconds(WhisparrSyncOptions.BackstopIntervalFloorSeconds);

    private static readonly TimeSpan DefaultInterval =
        TimeSpan.FromSeconds(WhisparrSyncOptions.DefaultBackstopIntervalSeconds);

    // UTC ticks read and written through Interlocked rather than a DateTimeOffset? field: the worker
    // writes these on its own thread while the host-configuration probe reads them on a request
    // thread, and a multi-word struct has no atomic read. Zero means never.
    private long _workerStartedAtUtcTicks;
    private long _workerCancelledAtUtcTicks;

    private DateTimeOffset? WorkerStartedAtUtc
        => InstantOf(Interlocked.Read(ref _workerStartedAtUtcTicks));

    private DateTimeOffset? WorkerCancelledAtUtc
        => InstantOf(Interlocked.Read(ref _workerCancelledAtUtcTicks));

    /// <summary>Runs until the host cancels <paramref name="ct"/>.</summary>
    /// <remarks>
    /// Every await takes <paramref name="ct"/>. The host stops this worker by cancelling the token
    /// and then blocking on the returned task, so an await that cannot be cancelled hangs host
    /// shutdown, disable and rebuild rather than failing. The cancellation is rethrown, because the
    /// host's catch for it is conditioned on the token being cancelled.
    /// <para>
    /// Every read in the body runs through <see cref="Cove.Extensions.Shared.RunAsSystem"/>: the
    /// worker carries no principal, and Cove's per-principal query filters answer an anonymous
    /// reader with zero rows and no error.
    /// </para>
    /// <para>
    /// Passes cannot overlap: there is one registration, the timer skips a wake rather than
    /// queueing it, and the pass is awaited inline. A detached call here would break that with
    /// nothing reporting it.
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public async Task RunAsync(IServiceProvider services, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);

        var clock = services.GetRequiredService<TimeProvider>();
        var scopes = services.GetRequiredService<IServiceScopeFactory>();
        var followUp = services.GetRequiredService<FollowUpScanCoalescer>();
        Interlocked.Exchange(ref _workerStartedAtUtcTicks, clock.GetUtcNow().UtcTicks);

        try
        {
            using var period = new PeriodicTimer(WorkerPeriod, clock);

            // Before every instant the clock can report, so the first wake after a start runs a pass.
            var lastPassStartedAt = DateTimeOffset.MinValue;

            while (await period.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // Before the interval gate: the live channel's batch is covered whether or not this
                // wake is a backstop wake, so the follow-up does not wait on the backstop interval.
                await ContainedAsync(
                    () => FollowUpAsync(scopes, followUp),
                    WhisparrSyncLog.FollowUpFaulted,
                    ct).ConfigureAwait(false);

                // Read each wake rather than once at the start, so a change to the interval takes
                // effect within one wake. A read that failed falls back to the default rather than
                // to a remembered value whose staleness nothing reports.
                var interval = await ContainedAsync(
                    () => RunAsSystem.RunInSystemScopeAsync(
                        scopes,
                        async scope => (await scope
                                .GetRequiredService<OptionsStore>()
                                .LoadAsync(ct)
                                .ConfigureAwait(false))
                            .BackstopInterval),
                    DefaultInterval,
                    WhisparrSyncLog.BackstopIntervalUnreadable,
                    ct).ConfigureAwait(false);

                if (clock.GetUtcNow() - lastPassStartedAt < interval)
                {
                    continue;
                }

                lastPassStartedAt = clock.GetUtcNow();
                await ContainedAsync(
                    () => PassAsync(scopes, ct),
                    WhisparrSyncLog.BackstopPassFaulted,
                    ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Dropped rather than flushed: a scan started after shutdown has begun reaches a host
            // that is stopping. The files are on disk and Cove's own library scan finds them.
            followUp.Drop();
            Interlocked.Exchange(ref _workerCancelledAtUtcTicks, clock.GetUtcNow().UtcTicks);
            throw;
        }
    }

    // The scope is opened only for a batch that is due, so a worker with nothing to cover resolves
    // no host service and touches no database.
    private static async Task FollowUpAsync(
        IServiceScopeFactory scopes, FollowUpScanCoalescer followUp)
    {
        if (!followUp.ScanIsDue)
        {
            return;
        }

        await RunAsSystem.RunInSystemScopeAsync(
            scopes,
            scope =>
            {
                followUp.FlushIfQuiet(scope.GetRequiredService<ICoveLibraryPort>());
                return Task.CompletedTask;
            }).ConfigureAwait(false);
    }

    private static async Task PassAsync(IServiceScopeFactory scopes, CancellationToken ct)
        => await RunAsSystem.RunInSystemScopeAsync(
                scopes, scope => scope.GetRequiredService<IBackstopPass>().RunAsync(ct))
            .ConfigureAwait(false);

    // The host treats anything but a cancellation as a fault and does not restart the worker, so
    // an exception let out of the body would stop the backstop until the extension is reloaded.
    // Every call the body makes comes through here. The timer wait stays outside: a failure there
    // is the host stopping this worker and has to reach the cancellation handling.
    private async Task<T> ContainedAsync<T>(
        Func<Task<T>> step, T whenContained, Action<ILogger, Exception> report, CancellationToken ct)
    {
        try
        {
            return await step().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Above the broad catch, so a shutdown classifies as cancelled rather than as a failure.
            // Conditioned on the token: an outbound timeout raises TaskCanceledException, which
            // derives from this, and the only handler a rethrow reaches admits a shutdown alone.
            throw;
        }
#pragma warning disable CA1031 // A step that failed unexpectedly must not take the worker with it.
        catch (Exception failure)
        {
            report(_log, failure);
            return whenContained;
        }
#pragma warning restore CA1031
    }

    private async Task ContainedAsync(
        Func<Task> step, Action<ILogger, Exception> report, CancellationToken ct)
        => await ContainedAsync<object?>(
                async () =>
                {
                    await step().ConfigureAwait(false);
                    return null;
                },
                null,
                report,
                ct)
            .ConfigureAwait(false);

    private static DateTimeOffset? InstantOf(long utcTicks)
        => utcTicks == 0 ? null : new DateTimeOffset(utcTicks, TimeSpan.Zero);
}
