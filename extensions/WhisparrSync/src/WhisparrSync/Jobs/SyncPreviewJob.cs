using System.Globalization;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Library;

namespace WhisparrSync.Jobs;

// Resolved when the run starts, not when it was enqueued: which instance is connected is a setting
// a person edits.
// Registers decides which count runs. Held and HeldSites are not interchangeable: one asks a batch
// of scene identifiers and the other a batch of stored studio identifiers.
// HeldSites raises rather than answering a short reading, because an identifier absent from both its
// sets would land in the not-yet-there column on the strength of nothing.
internal sealed record SyncPreviewAiming(
    WhisparrGeneration Generation,
    SyncRegisters Registers,
    Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>>? Held,
    Func<IReadOnlyCollection<string>, CancellationToken, Task<SiteBatchReading>>? HeldSites);

// The two sets are disjoint and neither is the whole batch: an identifier in neither is one the
// metadata source numbered and the instance holds no site for.
internal sealed record SiteBatchReading(
    IReadOnlySet<string> Held,
    IReadOnlySet<string> NamesNone);

/// <summary>
/// The count job's id, the batch size its comparison asks in, and the pass one count goes through.
/// </summary>
public static class SyncPreviewJob
{
    public const string JobId = "sync-preview";

    // The largest batch measured to answer 200 against whisparr:v3-3.3.8-release.1097 on 2026-09-10.
    // It also keeps a batch's worst-case answer inside WhisparrClient.MaxResponseBytes, which the
    // transport refuses outright rather than truncating: ChunkSize times MeasuredBytesPerHit is
    // about 2.3 MiB against an 8 MiB bound.
    internal const int ChunkSize = 1000;

    // What one answered entry costs, measured against the same instance and date. A constant so the
    // bound above is arithmetic a test can re-derive.
    internal const int MeasuredBytesPerHit = 2370;

    // A pacing bound, not a ceiling on how many reads are issued: every scene the reader owns on the
    // site is read. One, because the instance's request queue is the shared resource. A bound on how
    // many reads are issued would leave part of the library unmonitored and report a total that
    // reads like a complete one.
    internal const int SiteSceneReadsInFlight = 1;

    // A pacing bound, not a ceiling: every studio the library yields is resolved. Measured against a
    // 525-studio library on 2026-09-13: at four, 522 resolved and none was rate-limited; at eight,
    // 144 of the 525 were rejected.
    // Not a second rate bound. ProviderPacer already holds the rate to the host's metadata-server
    // setting. What it does not bound is how many callers wait: past its queue depth a caller is
    // refused immediately, which arrives here as a source that was not reached and ends the count.
    internal const int MetadataResolvesInFlight = 4;

    // Runs as System: the job carries no principal, and Cove's per-principal filters answer an
    // anonymous reader with zero rows and no error, so the library would read as empty.
    // Nothing per scene is held: the only collection alive at once is one batch of identifiers,
    // bounded by ChunkSize whatever the library holds.
    // A batch the instance did not answer throws and ends the whole count. The three counts arrive
    // together or not at all, because a count missing one of its numbers reads as a zero.
    internal static Task<SyncPreviewView?> RunAsync(
        IServiceScopeFactory scopes,
        Func<IServiceProvider, CancellationToken, Task<SyncPreviewAiming?>> aiming,
        ILogger log,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(aiming);

        return RunAsSystem.RunInSystemScopeAsync(scopes, async services =>
        {
            if (await aiming(services, ct).ConfigureAwait(false) is not { } aimed)
            {
                return null;
            }

            var identities = services.GetRequiredService<ILibrarySceneIdentityPort>();
            var counted = await CompareAsync(identities, aimed, log, ct).ConfigureAwait(false);

            services.GetRequiredService<SyncPreviewCache>().Hold(aimed.Generation, counted);
            return counted;
        });
    }

    // Counts, never a list of scenes: the line must not grow with the library.
    internal static string SummaryOf(SyncPreviewView counted)
    {
        ArgumentNullException.ThrowIfNull(counted);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{counted.NotYetThere:N0} not yet in Whisparr, {counted.AlreadyThere:N0} already there, "
                + $"{counted.Skipped:N0} carrying no metadata id.");
    }

    private static Task<SyncPreviewView> CompareAsync(
        ILibrarySceneIdentityPort identities,
        SyncPreviewAiming aimed,
        ILogger log,
        CancellationToken ct)
        => aimed.Registers switch
        {
            SyncRegisters.Scenes => CompareScenesAsync(identities, aimed, log, ct),
            SyncRegisters.Sites => CompareSitesAsync(identities, aimed, log, ct),
            _ => throw new ArgumentOutOfRangeException(
                nameof(aimed), aimed.Registers, "This is not a count this product takes."),
        };

    // Nothing caps how many batches are asked: a cap would answer a short pair that reads like a
    // complete one. What is bounded is MetadataResolvesInFlight.
    // Nothing per site is held: one batch of identifiers is alive at a time, bounded by ChunkSize
    // whatever the library holds, and the three answers are integers.
    private static async Task<SyncPreviewView> CompareSitesAsync(
        ILibrarySceneIdentityPort identities,
        SyncPreviewAiming aimed,
        ILogger log,
        CancellationToken ct)
    {
        if (aimed.HeldSites is not { } heldSites)
        {
            throw new InvalidOperationException(
                "A site count was aimed with no way to ask which sites are held.");
        }

        var notYetThere = 0;
        var alreadyThere = 0;
        var namesNone = 0;
        var batch = new List<string>(ChunkSize);

        try
        {
            await foreach (var site in identities
                .SiteIdentities(aimed.Generation, ct)
                .WithCancellation(ct)
                .ConfigureAwait(false))
            {
                batch.Add(site.RemoteId);
                if (batch.Count < ChunkSize)
                {
                    continue;
                }

                await AskAsync().ConfigureAwait(false);
            }

            if (batch.Count > 0)
            {
                await AskAsync().ConfigureAwait(false);
            }
        }
        // Ahead of the containment below, which names a shape a stop also arrives in: a stop read as
        // a count that did not finish would be reported as this product's own failure.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            WhisparrSyncLog.SyncCountDidNotFinish(log, WhisparrSyncLog.Classify(failure));
            throw new InvalidOperationException(
                "The count could not be finished, so no count was held.", failure);
        }

        if (namesNone > 0)
        {
            WhisparrSyncLog.StudiosTheSourceNamesNoSiteFor(log, namesNone);
        }

        return new SyncPreviewView(
            notYetThere,
            alreadyThere,
            namesNone
                + await identities.CountUnidentifiedSitesAsync(aimed.Generation, ct)
                    .ConfigureAwait(false),
            aimed.Registers,
            DateTimeOffset.UtcNow);

        async Task AskAsync()
        {
            var answered = await heldSites(batch, ct).ConfigureAwait(false);

            // Each offered identifier is classified rather than the answered set being counted. Two
            // studios carrying one identifier answer one number, and counting the answer's own size
            // would put the second of them in the not-yet-there column.
            foreach (var identity in batch)
            {
                if (answered.Held.Contains(identity))
                {
                    alreadyThere++;
                }
                else if (answered.NamesNone.Contains(identity))
                {
                    // Counted with the studios carrying no identifier at all, because a run can
                    // compose no add for either. In the not-yet-there column it would be offered
                    // for registration and the run would then refuse it.
                    namesNone++;
                }
                else
                {
                    notYetThere++;
                }
            }

            batch.Clear();
        }
    }

    private static async Task<SyncPreviewView> CompareScenesAsync(
        ILibrarySceneIdentityPort identities,
        SyncPreviewAiming aimed,
        ILogger log,
        CancellationToken ct)
    {
        if (aimed.Held is not { } held)
        {
            throw new InvalidOperationException(
                "A scene count was aimed with no way to ask which scenes are held.");
        }

        var notYetThere = 0;
        var alreadyThere = 0;
        var batch = new List<string>(ChunkSize);

        try
        {
            await foreach (var identity in identities
                .SceneIdentities(aimed.Generation, ct)
                .WithCancellation(ct)
                .ConfigureAwait(false))
            {
                batch.Add(identity);
                if (batch.Count < ChunkSize)
                {
                    continue;
                }

                await AskAsync().ConfigureAwait(false);
            }

            if (batch.Count > 0)
            {
                await AskAsync().ConfigureAwait(false);
            }
        }
        // Ahead of the containment below, for the reason the site comparison's own rethrow states.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            WhisparrSyncLog.SyncCountDidNotFinish(log, WhisparrSyncLog.Classify(failure));
            throw new InvalidOperationException(
                "The count could not be finished, so no count was held.", failure);
        }

        return new SyncPreviewView(
            notYetThere,
            alreadyThere,
            await identities.CountUnidentifiedAsync(aimed.Generation, ct).ConfigureAwait(false),
            aimed.Registers,
            DateTimeOffset.UtcNow);

        async Task AskAsync()
        {
            var answered = await held(batch, ct).ConfigureAwait(false);

            // Each offered identifier is classified rather than the answered set being counted.
            // Two spellings of one source yield one identifier twice, and counting the answer's own
            // size would put the second copy of a held scene in the not-yet-there column.
            var wasHeld = batch.Count(answered.Contains);
            alreadyThere += wasHeld;
            notYetThere += batch.Count - wasHeld;
            batch.Clear();
        }
    }
}
