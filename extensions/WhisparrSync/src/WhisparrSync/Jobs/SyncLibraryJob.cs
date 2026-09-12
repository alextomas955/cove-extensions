using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;

namespace WhisparrSync.Jobs;

/// <summary>What one enqueued library run was asked for, as the host's map held it.</summary>
/// <remarks>
/// One member, because a library run is aimed at the whole library and there is nothing else a
/// caller could name. Which instance it offers to is resolved when the run starts.
/// </remarks>
/// <param name="AlsoMonitor">Whether the reader asked for what it offers to be marked wanted.</param>
public sealed record SyncLibraryBatch(bool AlsoMonitor);

/// <summary>What one library run needs from the connected instance, already aimed at it.</summary>
/// <remarks>
/// Resolved when the run starts rather than when it was asked for. The profile and the root each
/// registration carries are the instance's to change at any time, and a run enqueued minutes ago
/// must not create catalogue items under values read before that.
/// </remarks>
/// <remarks>
/// Which pass runs follows from <paramref name="Registers"/>, and the delegate that pass needs is
/// the one supplied. A pass named with no delegate for it is a construction fault rather than a
/// generation gap, so the run throws on it rather than reporting an empty library.
/// </remarks>
/// <param name="Generation">Whose namespace the library's own identifiers are read under.</param>
/// <param name="Registers">Which pass the run makes, which is what the capability decided.</param>
/// <param name="RegisterScene">Offers one scene, answering what it established.</param>
/// <param name="RegisterSite">Registers one site, answering what it established.</param>
/// <param name="Monitor">
/// Marks one offered scene wanted, or null where the reader did not ask for it or the generation
/// registers no per-scene monitor.
/// </param>
/// <param name="MonitorSiteScenes">
/// Marks the scenes the reader owns on one registered site wanted, or null where the reader did not
/// ask for it, the generation registers no per-scene monitor, or the connected metadata provider
/// issues no number to address a scene by.
/// </param>
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
/// <remarks>
/// <see cref="Decode"/> is total: it is read inside the host's job runner, where a throw is a
/// faulted job rather than a handled answer, so a run nobody can read offers nothing.
/// </remarks>
public static class SyncLibraryJob
{
    /// <summary>The job id this extension's own type prefix is minted onto.</summary>
    /// <remarks>
    /// Read from the one declaration beside the routes rather than restated here. The count route
    /// derives whether a run is in flight from that same constant, so a second literal could let the
    /// route answer about a job type nothing enqueues while both files passed their own tests.
    /// </remarks>
    public const string JobId = global::WhisparrSync.WhisparrSync.SyncLibraryJobId;

    private const string AlsoMonitorKey = "alsoMonitor";

    /// <summary>What the run reports when it could not be aimed at an instance at all.</summary>
    /// <remarks>
    /// Its own sentence rather than the planner's. A run that reached no instance is a different fact
    /// from a library carrying no identifier, and a reader acts on the two differently.
    /// </remarks>
    internal const string NoInstanceLine = "No Whisparr is connected, so no scene was offered.";

    /// <summary>Encodes one library run onto the host's parameter map.</summary>
    public static Dictionary<string, string> Encode(bool alsoMonitor)
        => new(StringComparer.Ordinal)
        {
            [AlsoMonitorKey] = alsoMonitor ? "true" : "false",
        };

    /// <summary>Reads one library run back off the host's parameter map.</summary>
    /// <remarks>
    /// Never throws. A null map, a missing key and a value that is not a boolean all answer that
    /// monitoring was not asked for, which is the reading that acts less rather than more.
    /// </remarks>
    public static SyncLibraryBatch Decode(IReadOnlyDictionary<string, string>? parameters)
        => new(
            parameters is not null
            && parameters.TryGetValue(AlsoMonitorKey, out var value)
            && bool.TryParse(value, out var asked)
            && asked);

    /// <summary>
    /// Offers every identified scene in the library to the instance through
    /// <paramref name="aiming"/>, inside one scope elevated to System.
    /// </summary>
    /// <remarks>
    /// The run carries no principal of its own, and Cove's per-principal query filters answer an
    /// anonymous reader with zero rows and no error, which here would register nothing at all while
    /// reporting an empty library.
    /// <para>
    /// The identifier stream is handed over as a factory and never as a collection: the planner walks
    /// it twice, and a library reaches millions of files. The host's own batching helper is not used
    /// on this path at all, because it materializes its unit sequence before the first request.
    /// </para>
    /// </remarks>
    /// <param name="batch">What the run was asked for.</param>
    /// <param name="scopes">The scope factory the extension was handed at initialization.</param>
    /// <param name="aiming">
    /// What the run needs from the connected instance, over the run's own elevated services, or null
    /// where it must not act.
    /// </param>
    /// <param name="progress">The host's own progress, which the scenes are reported on.</param>
    /// <param name="ct">Cancelled when the host stops the job.</param>
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

    /// <summary>
    /// <paramref name="offer"/>, or the fault of a pass named with no way to make it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="offer"/> is null. That says nothing about a generation, so it is not
    /// expressible as an empty library or as a refusal.
    /// </exception>
    private static Func<TIdentity, CancellationToken, Task<SyncRegistration>> Supplied<TIdentity>(
        Func<TIdentity, CancellationToken, Task<SyncRegistration>>? offer, SyncRegisters registers)
        => offer
            ?? throw new InvalidOperationException(
                $"A run naming {registers} was aimed with no way to register one.");
}
