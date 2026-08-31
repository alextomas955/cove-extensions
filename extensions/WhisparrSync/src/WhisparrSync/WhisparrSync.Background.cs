using Cove.Extensions.Shared;
using Cove.Plugins;
using Microsoft.Extensions.DependencyInjection;
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
        Interlocked.Exchange(ref _workerStartedAtUtcTicks, clock.GetUtcNow().UtcTicks);

        try
        {
            using var period = new PeriodicTimer(WorkerPeriod, clock);

            // Before every instant the clock can report, so the first wake after a start runs a pass.
            var lastPassStartedAt = DateTimeOffset.MinValue;

            while (await period.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // Read each wake rather than once at the start, so a change to the interval takes
                // effect within one wake instead of after up to a whole interval of the old value.
                var interval = await RunAsSystem.RunInSystemScopeAsync(
                    scopes,
                    async scope => (await scope
                            .GetRequiredService<OptionsStore>()
                            .LoadAsync(ct)
                            .ConfigureAwait(false))
                        .BackstopInterval).ConfigureAwait(false);

                if (clock.GetUtcNow() - lastPassStartedAt < interval)
                {
                    continue;
                }

                lastPassStartedAt = clock.GetUtcNow();
                await PassAsync(scopes, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _workerCancelledAtUtcTicks, clock.GetUtcNow().UtcTicks);
            throw;
        }
    }

    /// <summary>Runs one pass, and keeps the worker alive through a failure it did not expect.</summary>
    /// <remarks>
    /// The host treats anything but a cancellation as a fault and does not restart the worker, so an
    /// exception let out here would stop the backstop until the extension is reloaded.
    /// </remarks>
    private async Task PassAsync(IServiceScopeFactory scopes, CancellationToken ct)
    {
        try
        {
            await RunAsSystem.RunInSystemScopeAsync(
                scopes,
                scope => scope.GetRequiredService<IBackstopPass>().RunAsync(ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Above the broad catch, so a shutdown classifies as cancelled rather than as a failure.
            throw;
        }
#pragma warning disable CA1031 // A pass that failed unexpectedly must not take the worker with it.
        catch (Exception failure)
        {
            WhisparrSyncLog.BackstopPassFaulted(_log, failure);
        }
#pragma warning restore CA1031
    }

    private static DateTimeOffset? InstantOf(long utcTicks)
        => utcTicks == 0 ? null : new DateTimeOffset(utcTicks, TimeSpan.Zero);
}
