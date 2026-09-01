using Cove.Extensions.Shared;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Import;
using WhisparrSync.Options;

namespace WhisparrSync;

/// <summary>
/// The long-lived worker the host starts after this extension initializes and cancels when it is
/// disabled, uninstalled, or the host shuts down.
/// </summary>
/// <remarks>
/// <see cref="IBackgroundExtension"/> is declared here rather than inherited: unlike the data and
/// stateful capabilities, <see cref="Cove.Sdk.FullExtensionBase"/> does not implement it.
/// </remarks>
public sealed partial class WhisparrSync : IBackgroundExtension
{
    /// <summary>How long the worker waits between wakes.</summary>
    /// <remarks>
    /// A pass cannot run more often than a wake, so this period is also the backstop interval's
    /// floor, and the two are one value rather than two that can drift apart.
    /// </remarks>
    private static readonly TimeSpan WorkerPeriod =
        TimeSpan.FromSeconds(WhisparrSyncOptions.BackstopIntervalFloorSeconds);

    /// <summary>The interval a wake works to when the stored one could not be read.</summary>
    private static readonly TimeSpan DefaultInterval =
        TimeSpan.FromSeconds(WhisparrSyncOptions.DefaultBackstopIntervalSeconds);

    // UTC ticks read and written through Interlocked rather than a DateTimeOffset? field: the worker
    // writes these on its own thread while the host-configuration probe reads them on a request
    // thread, and a multi-word struct has no atomic read. Zero means never.
    private long _workerStartedAtUtcTicks;
    private long _workerCancelledAtUtcTicks;

    /// <summary>When the host last started this worker, or null when it never has.</summary>
    private DateTimeOffset? WorkerStartedAtUtc
        => InstantOf(Interlocked.Read(ref _workerStartedAtUtcTicks));

    /// <summary>When this worker's token was last cancelled, or null when it never was.</summary>
    private DateTimeOffset? WorkerCancelledAtUtc
        => InstantOf(Interlocked.Read(ref _workerCancelledAtUtcTicks));

    /// <summary>Runs until the host cancels <paramref name="ct"/>.</summary>
    /// <remarks>
    /// Every await takes <paramref name="ct"/>. The host stops this worker by cancelling the token and
    /// then BLOCKING on the returned task, so an await that cannot be cancelled hangs host shutdown,
    /// disable and rebuild rather than failing.
    /// <para>
    /// The cancellation is rethrown. The host's catch for it is conditioned on the token being
    /// cancelled, so a rethrow classifies the stop as cancelled and anything else as faulted.
    /// </para>
    /// <para>
    /// Every read in the body runs through <see cref="Cove.Extensions.Shared.RunAsSystem"/>: the
    /// worker carries no principal, and Cove's per-principal query filters answer an Anonymous reader
    /// with zero rows and no error.
    /// </para>
    /// <para>
    /// Passes cannot overlap, and that is a property of this shape rather than of a guard. There is one
    /// registration, the timer skips a wake rather than queueing it, and the pass is awaited inline. A
    /// detached call here would break it with nothing reporting, and a lock added beside it would read
    /// as load-bearing while guarding nothing.
    /// </para>
    /// </remarks>
    /// <param name="services">The extension's own provider, leased for the life of this worker.</param>
    /// <param name="ct">Cancelled when the host stops this worker.</param>
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
                    WhisparrSyncLog.FollowUpFaulted).ConfigureAwait(false);

                // Read each wake rather than once at the start, so a change to the interval takes
                // effect within one wake instead of after up to a whole interval of the old value.
                // A read that failed costs the wakes the default covers: an interval remembered from
                // an earlier wake would be a value whose staleness nothing reports.
                var interval = await ContainedAsync(
                    () => RunAsSystem.RunInSystemScopeAsync(
                        scopes,
                        async scope => (await scope
                                .GetRequiredService<OptionsStore>()
                                .LoadAsync(ct)
                                .ConfigureAwait(false))
                            .BackstopInterval),
                    DefaultInterval,
                    WhisparrSyncLog.BackstopIntervalUnreadable).ConfigureAwait(false);

                if (clock.GetUtcNow() - lastPassStartedAt < interval)
                {
                    continue;
                }

                lastPassStartedAt = clock.GetUtcNow();
                await ContainedAsync(
                    () => PassAsync(scopes, ct),
                    WhisparrSyncLog.BackstopPassFaulted).ConfigureAwait(false);
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

    /// <summary>Starts one scan over the live channel's batch, once it has been quiet.</summary>
    /// <remarks>
    /// The scope is opened only for a batch that is due, so a worker with nothing to cover resolves
    /// no host service and touches no database.
    /// </remarks>
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

    /// <summary>Runs one pass.</summary>
    /// <remarks>The pass reports its own reading; nothing in the loop reads the returned one.</remarks>
    private static async Task PassAsync(IServiceScopeFactory scopes, CancellationToken ct)
        => await RunAsSystem.RunInSystemScopeAsync(
                scopes, scope => scope.GetRequiredService<IBackstopPass>().RunAsync(ct))
            .ConfigureAwait(false);

    /// <summary>
    /// Runs one step of the loop body, and keeps the worker alive through a failure it did not expect.
    /// </summary>
    /// <remarks>
    /// The host treats anything but a cancellation as a fault and does not restart the worker, so an
    /// exception let out of the body would stop the backstop until the extension is reloaded. Every
    /// call the body makes comes through here: a guard around one of them leaves the others able to
    /// end the worker, and which call the failure came from is not something a user can see.
    /// <para>
    /// The timer wait is deliberately outside. A failure there is the host stopping this worker and
    /// has to reach the cancellation handling.
    /// </para>
    /// </remarks>
    /// <param name="step">The call to contain.</param>
    /// <param name="whenContained">What the step reads as when it failed.</param>
    /// <param name="report">The line the failure is reported in.</param>
    private async Task<T> ContainedAsync<T>(
        Func<Task<T>> step, T whenContained, Action<ILogger, Exception> report)
    {
        try
        {
            return await step().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Above the broad catch, so a shutdown classifies as cancelled rather than as a failure.
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

    /// <inheritdoc cref="ContainedAsync{T}"/>
    private async Task ContainedAsync(Func<Task> step, Action<ILogger, Exception> report)
        => await ContainedAsync<object?>(
                async () =>
                {
                    await step().ConfigureAwait(false);
                    return null;
                },
                null,
                report)
            .ConfigureAwait(false);

    private static DateTimeOffset? InstantOf(long utcTicks)
        => utcTicks == 0 ? null : new DateTimeOffset(utcTicks, TimeSpan.Zero);
}
