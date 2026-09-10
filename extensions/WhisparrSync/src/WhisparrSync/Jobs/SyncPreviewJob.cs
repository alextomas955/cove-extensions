using System.Globalization;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Contracts;
using WhisparrSync.Library;

namespace WhisparrSync.Jobs;

/// <summary>What one count needs from the connected instance, already aimed at it.</summary>
/// <remarks>
/// Resolved when the run STARTS rather than when it was asked for. Which instance is connected is a
/// setting a person edits, and a count enqueued minutes ago must not compare against an address read
/// before that.
/// </remarks>
/// <param name="Generation">Whose namespace the library's own scene identifiers are read under.</param>
/// <param name="Registers">What a run against this instance would register in it.</param>
/// <param name="Held">Which of one batch of identifiers the instance already holds.</param>
internal sealed record SyncPreviewAiming(
    WhisparrGeneration Generation,
    SyncRegisters Registers,
    Func<IReadOnlyCollection<string>, CancellationToken, Task<IReadOnlySet<string>>> Held);

/// <summary>
/// The count job's id, the batch size its comparison asks in, and the pass one count goes through.
/// </summary>
public static class SyncPreviewJob
{
    /// <summary>The job id this extension's own type prefix is minted onto.</summary>
    public const string JobId = "sync-preview";

    /// <summary>How many identifiers one comparison request asks about.</summary>
    /// <remarks>
    /// 1,000 is the largest size measured to answer 200 against
    /// whisparr:v3-3.3.8-release.1097 on 2026-09-10; 250 and 1,000 were both accepted.
    /// <para>
    /// It also has to keep a batch's worst-case answer inside
    /// <see cref="Whisparr.WhisparrClient.MaxResponseBytes"/>, because the transport refuses a
    /// larger answer outright rather than truncating it. An answered entry measures about
    /// <see cref="MeasuredBytesPerHit"/> bytes, so a full batch answered in its entirety is about
    /// 2.3 MiB against an 8 MiB bound.
    /// </para>
    /// </remarks>
    internal const int ChunkSize = 1000;

    /// <summary>What one answered entry costs, measured against the same instance and date.</summary>
    /// <remarks>
    /// Held as a constant so the bound above is arithmetic a test can re-derive, rather than a
    /// figure written once in prose and never checked again.
    /// </remarks>
    internal const int MeasuredBytesPerHit = 2370;

    /// <summary>
    /// Counts the library's identified scenes against what the instance holds, inside ONE scope
    /// elevated to System, and holds the result.
    /// </summary>
    /// <remarks>
    /// The run carries no principal of its own, and Cove's per-principal query filters answer an
    /// anonymous reader with zero rows and no error, which here would report an empty library.
    /// <para>
    /// Nothing per scene is held: the only collection alive at once is one batch of identifiers,
    /// bounded by <see cref="ChunkSize"/> whatever the library holds.
    /// </para>
    /// <para>
    /// A batch the instance did not answer ends the whole count. Three counts arrive together or not
    /// at all, because a count missing one of its numbers reads as a zero.
    /// </para>
    /// </remarks>
    /// <param name="scopes">The scope factory the extension was handed at initialization.</param>
    /// <param name="aiming">
    /// What the count needs from the connected instance, over the run's own elevated services, or
    /// null where there is nothing to compare against.
    /// </param>
    /// <param name="log">This extension's own logger.</param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
    /// <exception cref="InvalidOperationException">
    /// A batch could not be compared, so no count was held.
    /// </exception>
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

    /// <summary>The one line the host's job list shows for <paramref name="counted"/>.</summary>
    /// <remarks>
    /// Counts rather than a list of scenes, and it says nothing about the entries the instance
    /// creates by itself alongside them: the three numbers are what a reader acts on.
    /// </remarks>
    internal static string SummaryOf(SyncPreviewView counted)
    {
        ArgumentNullException.ThrowIfNull(counted);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{counted.NotYetThere:N0} not yet in Whisparr, {counted.AlreadyThere:N0} already there, "
                + $"{counted.Skipped:N0} carrying no metadata id.");
    }

    private static async Task<SyncPreviewView> CompareAsync(
        ILibrarySceneIdentityPort identities,
        SyncPreviewAiming aimed,
        ILogger log,
        CancellationToken ct)
    {
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
        catch (Exception failure) when (failure is HttpRequestException or IOException)
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
            var answered = await aimed.Held(batch, ct).ConfigureAwait(false);

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
