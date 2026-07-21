using Cove.Core.Interfaces;

namespace WhisparrSync.Reconcile;

/// <summary>
/// The self-scheduled reconcile loop: no host cron exists for extensions, so the extension owns a
/// <see cref="PeriodicTimer"/> started in <c>InitializeAsync</c> and cancelled in <c>ShutdownAsync</c>. Each
/// tick enqueues an EXCLUSIVE <see cref="IJobService"/> job running the captured reconcile work, so passes
/// serialize (never unbounded-concurrent) and show in the host task list.
/// </summary>
internal sealed class ReconcileScheduler(
    IJobService? jobs,
    Func<IJobProgress, CancellationToken, Task> work,
    TimeSpan interval)
{
    private const string JobDescription = "Whisparr reconcile";

    /// <summary>
    /// Enqueues one exclusive reconcile job. Extracted from <see cref="RunLoopAsync"/> so a single tick is
    /// unit-testable without the timer. A no-op when the host supplied no <see cref="IJobService"/>.
    /// </summary>
    public void EnqueueOnce()
        => jobs?.Enqueue(ReconcileJob.JobId, JobDescription, work, exclusive: true);

    /// <summary>
    /// Ticks on the interval until <paramref name="ct"/> is cancelled, enqueuing one reconcile job per tick.
    /// A per-tick fault never breaks the loop (the next tick still fires); cancellation ends it cleanly.
    /// </summary>
    public async Task RunLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                EnqueueOnce();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single enqueue fault must not kill the backstop loop; the next tick retries.
            }
        }
    }
}
