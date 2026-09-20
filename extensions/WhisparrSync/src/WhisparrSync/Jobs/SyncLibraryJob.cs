using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Jobs;

/// <summary>What one enqueued library run was asked for.</summary>
/// <remarks>Which instance the run offers to is resolved when the run starts, not from here.</remarks>
public sealed record SyncLibraryBatch(bool AlsoMonitor);

// Resolved when the run starts, not when it was enqueued: the instance can change the profile and
// the root at any time.
// Registers decides which pass runs. A pass named with no delegate for it is a construction fault,
// not a generation gap, so the run throws rather than reporting an empty library.
internal sealed record SyncLibraryAiming(
    WhisparrGeneration Generation,
    SyncRegisters Registers,
    Func<string, CancellationToken, Task<SyncRegistration>>? RegisterScene,
    Func<LibrarySiteIdentity, CancellationToken, Task<SyncRegistration>>? RegisterSite,
    Func<string, SyncRegistration, CancellationToken, Task<SceneMonitorTally>>? Monitor,
    Func<LibrarySiteIdentity, SyncRegistration, CancellationToken, Task<SceneMonitorTally>>?
        MonitorSiteScenes = null);

/// <summary>
/// The library run's id, its (de)serialization onto the host's string-only parameter map, and the
/// elevation the streamed scene loop runs inside.
/// </summary>
public static class SyncLibraryJob
{
    // Read from the one declaration beside the routes, never restated. The count route derives
    // whether a run is in flight from the same constant, so a second literal could let the route
    // answer about a job type nothing enqueues.
    public const string JobId = global::WhisparrSync.WhisparrSync.SyncLibraryJobId;

    private const string AlsoMonitorKey = "alsoMonitor";

    // Its own sentence, not the planner's: a run that reached no instance is a different fact from
    // a library carrying no identifier.
    internal const string NoInstanceLine = "No Whisparr is connected, so no scene was offered.";

    public static Dictionary<string, string> Encode(bool alsoMonitor)
        => new(StringComparer.Ordinal)
        {
            [AlsoMonitorKey] = alsoMonitor ? "true" : "false",
        };

    /// <summary>Reads one library run back off the host's parameter map.</summary>
    /// <remarks>
    /// Never throws; it runs inside the host's job runner, where a throw faults the job. Anything
    /// unreadable answers that monitoring was not asked for, the reading that acts less.
    /// </remarks>
    public static SyncLibraryBatch Decode(IReadOnlyDictionary<string, string>? parameters)
        => new(
            parameters is not null
            && parameters.TryGetValue(AlsoMonitorKey, out var value)
            && bool.TryParse(value, out var asked)
            && asked);

    // Runs as System: the job carries no principal, and Cove's per-principal filters answer an
    // anonymous reader with zero rows and no error, so the library would read as empty.
    // The identifier stream is handed over as a factory, never as a collection: the planner walks it
    // twice and libraries reach millions of files. The host's batching helper is not used here,
    // because it materializes its unit sequence before the first request.
    internal static Task<SyncLibraryRun> RunAsync(
        SyncLibraryBatch batch,
        IServiceScopeFactory scopes,
        Func<IServiceProvider, SyncLibraryBatch, CancellationToken, Task<SyncLibraryAiming?>> aiming,
        IJobProgress progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(aiming);
        ArgumentNullException.ThrowIfNull(progress);

        return RunAsSystem.RunInSystemScopeAsync(scopes, async services =>
        {
            if (await aiming(services, batch, ct).ConfigureAwait(false) is not { } aimed)
            {
                progress.SetSummary(NoInstanceLine);
                return SyncLibraryPlanner.Nothing;
            }

            var identities = services.GetRequiredService<ILibrarySceneIdentityPort>();

            return aimed.Registers switch
            {
                SyncRegisters.Scenes => await SyncLibraryPlanner.RunAsync(
                    aimed.Registers,
                    runCt => identities.SceneIdentities(aimed.Generation, runCt),
                    identity => identity,
                    Supplied(aimed.RegisterScene, aimed.Registers),
                    aimed.Monitor,
                    progress,
                    ct).ConfigureAwait(false),

                // Nothing monitors the site itself. What the reader owns on a site is its scenes, so
                // the monitor slot here marks those rather than the site, and it is null unless the
                // reader asked and every role that pass needs was obtained.
                SyncRegisters.Sites => await SyncLibraryPlanner.RunAsync<LibrarySiteIdentity>(
                    aimed.Registers,
                    runCt => identities.SiteIdentities(aimed.Generation, runCt),
                    site => site.RemoteId,
                    Supplied(aimed.RegisterSite, aimed.Registers),
                    aimed.MonitorSiteScenes,
                    progress,
                    ct).ConfigureAwait(false),

                _ => throw new InvalidOperationException(
                    $"{aimed.Registers} is not a pass this run makes."),
            };
        });
    }

    // A null offer says nothing about a generation, so it is not expressible as an empty library or
    // as a refusal.
    private static Func<TIdentity, CancellationToken, Task<SyncRegistration>> Supplied<TIdentity>(
        Func<TIdentity, CancellationToken, Task<SyncRegistration>>? offer, SyncRegisters registers)
        => offer
            ?? throw new InvalidOperationException(
                $"A run naming {registers} was aimed with no way to register one.");
}
