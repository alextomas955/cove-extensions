using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Client;
using WhisparrSync.Ingest;
using WhisparrSync.Matching;
using WhisparrSync.State;

namespace WhisparrSync;

/// <summary>
/// The reconcile work delegate the self-scheduled loop enqueues each tick (see <c>WhisparrSync.cs</c>). One
/// pass runs the <see cref="ReconcileJob"/> over the SAME <see cref="IngestCoordinator"/> + fail-closed
/// Whisparr-root guard the webhook uses, so the polling backstop and the webhook share one idempotent ingest
/// path.
/// </summary>
public sealed partial class WhisparrSync
{
    /// <summary>
    /// One reconcile pass, run inside the enqueued exclusive job (the scheduler's work delegate). Resolves the
    /// stored creds (stored key only against the stored host) and the request-scoped
    /// <see cref="WhisparrClient"/>, then runs the <see cref="ReconcileJob"/> over the SAME
    /// <see cref="IngestCoordinator"/> + Whisparr-root guard the webhook uses. A no-op until configured.
    /// </summary>
    internal async Task RunReconcileAsync(CancellationToken ct)
    {
        await using var scope = ScopeFactory.CreateAsyncScope();
        var client = scope.ServiceProvider.GetRequiredService<WhisparrClient>();

        var (_, baseUrl, apiKey) = await StoredCredsAsync(ct);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrEmpty(apiKey))
        {
            return; // not configured yet — nothing to reconcile against
        }

        // Deliberately the untapped root read, unlike the webhook's. The coordinator consults it once per
        // RECORD, so observing it here would be a store write per history row; this pass already proves the
        // acquisition side's reachability once per page through the history delegate below.
        var coordinator = new IngestCoordinator(
            ScopeFactory, async c => (await RootsPort.ReadAsync(client, c)).Paths);
        var job = new ReconcileJob(
            Store,
            coordinator,
            (page, c) => ObservedHistoryPageAsync(client, baseUrl, apiKey, page, c),
            reason => LogHealthRecordFailed(HealthDependency.Import, reason));
        await job.RunAsync(ct);
    }

    /// <summary>
    /// One history page, with its already-classified transport outcome recorded on the way past and the result
    /// returned unchanged.
    /// </summary>
    /// <remarks>
    /// Wrapping at the DELEGATE site is what keeps <see cref="ReconcileJob"/> itself untouched — its not-ok
    /// branch, its checkpoint retention and its paging-gap reasoning all stay exactly as shipped. This is the
    /// fifteen-minute heartbeat, the one path that proves reachability with no user present.
    /// </remarks>
    private async Task<WhisparrResult<WhisparrHistoryPage>> ObservedHistoryPageAsync(
        WhisparrClient client, string baseUrl, string apiKey, int page, CancellationToken ct)
    {
        var result = await client.ListHistoryAsync(baseUrl, apiKey, page, ReconcileJob.PageSize, ct);
        await HealthStore.TryRecordAsync(
            Store,
            HealthDependency.Acquisition,
            HealthOutcome.FromAcquisition(result.State, result.Reason),
            reason => LogHealthRecordFailed(HealthDependency.Acquisition, reason),
            ct);
        return result;
    }
}
