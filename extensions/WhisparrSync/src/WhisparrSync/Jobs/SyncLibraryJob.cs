using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
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
// Link carries the same aim an entity's own reflect-owned run is given, or the reason the instance
// refused one. A null Link means no reflect-owned role was obtained at all, which is a generation
// gap rather than a refusal and states nothing to a reader.
internal sealed record SyncLibraryAiming(
    WhisparrGeneration Generation,
    SyncRegisters Registers,
    Func<string, string?, CancellationToken, Task<SyncRegistration>>? RegisterScene,
    Func<LibrarySiteIdentity, CancellationToken, Task<SyncRegistration>>? RegisterSite,
    Func<string, SyncRegistration, CancellationToken, Task<SceneMonitorTally>>? Monitor,
    Func<LibrarySiteIdentity, SyncRegistration, CancellationToken, Task<SceneMonitorTally>>?
        MonitorSiteScenes = null,
    ReflectOwnedAim? Link = null,
    IReadOnlyList<string>? RootOrder = null,
    IReadOnlyList<string>? OutOfReach = null);

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

            // A folder is linked as the walk leaves it, so files attach from the first folder
            // rather than after the whole library has been offered, and a stop keeps what it
            // linked. Null where the pass links nothing, which leaves the walk registering only.
            var linking = await LinkingThrough(services, aimed, ct).ConfigureAwait(false);

            // The roots this instance can reach are walked first, so a library whose other half is
            // out of reach still links something within the first folder rather than the last.
            // Empty where nothing links and so nothing needed the roots probed: the walk then takes
            // every folder in path order, which is the order it had before any root was ranked.
            var rootOrder = aimed.RootOrder ?? [];

            var registered = await RegisterAsync(aimed, identities, linking, rootOrder, progress, ct)
                .ConfigureAwait(false);

            if (aimed.Link is not { } link)
            {
                return registered;
            }

            progress.SetSummary(string.Join(
                ' ',
                SyncLibraryPlanner.SummaryOf(registered, aimed.Monitor is not null, aimed.Registers),
                ReflectOwnedJob.SummaryOf(
                    linking?.Total ?? ReflectOwnedJob.Untaken with { Skipped = link.Skipped })));

            return registered;
        });
    }

    // Null where nothing links: a pass registering studios, an instance holding no reflect-owned
    // role, or a hard-link setting that refused. A declared-root list that could not be read gives
    // one that carries that fact and attaches nothing, because an import made without the
    // comparison copies the bytes rather than linking them. The roots are read once here rather
    // than once per folder.
    private static async Task<FolderLinking?> LinkingThrough(
        IServiceProvider services, SyncLibraryAiming aimed, CancellationToken ct)
    {
        if (aimed.Link?.Through is not { } aim)
        {
            return null;
        }

        var instanceRoots = await services.GetRequiredService<IReportedRootPort>()
            .ReadAsync(aimed.Generation, ct).ConfigureAwait(false);

        var outOfReach = aimed.OutOfReach ?? [];

        return instanceRoots is null
            ? new FolderLinking(aim, [], outOfReach, acts: false)
            {
                Total = new ReflectOwnedRun(
                    ReflectOwnedRunOutcome.Completed, 0, 0, RootsCouldNotBeRead: true),
            }
            : new FolderLinking(aim, instanceRoots, outOfReach, acts: true);
    }

    // Carries the total across a walk rather than answering one per folder, so the run states one
    // line however many folders it passed through.
    //
    // What the walk registered in the folder it is inside is held by the identity Cove knows each
    // scene by, and dropped as the walk leaves, so nothing here grows with the library: it holds one
    // entry per scene in one directory.
    private sealed class FolderLinking(
        ReflectOwnedAiming aim,
        IReadOnlyList<string> instanceRoots,
        IReadOnlyList<string> outOfReach,
        bool acts)
    {
        private readonly Dictionary<string, RegisteredScene> _registeredHere =
            new(StringComparer.Ordinal);

        internal ReflectOwnedRun Total { get; set; } = ReflectOwnedJob.Untaken;

        // The id the instance answered with, against the identity the offer named. A refusal carries
        // no id and is not held, so a file of that scene is left to the instance's own reading.
        internal void Registered(string remoteId, SyncRegistration answered)
        {
            ArgumentNullException.ThrowIfNull(answered);

            if (MonitoringProjector.EntityIdIn(answered.Answer?.Body) is { } entityId)
            {
                _registeredHere[remoteId] = new RegisteredScene(
                    entityId, MonitoringProjector.PathIn(answered.Answer?.Body));
            }
        }

        // The declared root on the same filesystem as this folder, or null where none is. Addressed
        // through the same held reading every folder under that root uses, so this costs no request
        // beyond the first folder under each.
        internal async Task<string?> RootReachingAsync(string folder, CancellationToken ct)
        {
            if (!acts)
            {
                return null;
            }

            var addressed = await aim.Address(folder, ct).ConfigureAwait(false);
            return addressed.InstancePath is { } onInstance
                ? AddDefaultsProjector.RootReachingFrom(onInstance, instanceRoots)
                : null;
        }

        internal async Task LinkAsync(string folder, CancellationToken ct)
        {
            var registered =
                new Dictionary<string, RegisteredScene>(_registeredHere, StringComparer.Ordinal);
            _registeredHere.Clear();

            if (!acts || UnderAnUnreachableRoot(folder))
            {
                return;
            }

            Total = Total.Plus(
                await ReflectOwnedJob
                    .LinkOneFolderAsync(aim, instanceRoots, folder, registered, ct)
                    .ConfigureAwait(false));
        }

        private bool UnderAnUnreachableRoot(string folder)
            => outOfReach.Any(root => PathCandidateGuard.TailBelow(folder, root) is not null);
    }

    private static async Task<SyncLibraryRun> RegisterAsync(
        SyncLibraryAiming aimed,
        ILibrarySceneIdentityPort identities,
        FolderLinking? linking,
        IReadOnlyList<string> rootOrder,
        IJobProgress progress,
        CancellationToken ct)
        => aimed.Registers switch
        {
            SyncRegisters.Scenes => await SyncLibraryPlanner.RunAsync(
                aimed.Registers,
                new SyncLibrarySource<LibrarySceneInFolder>(
                    runCt => identities.SceneIdentitiesByFolder(aimed.Generation, rootOrder, runCt),
                    row => row.RemoteId ?? string.Empty,
                    Registering(aimed, linking),
                    Monitoring(aimed)),
                progress,
                ct,
                new SyncLibraryWalk<LibrarySceneInFolder>(
                    Offers: row => row.RemoteId is not null,
                    FolderOf: row => row.Folder,
                    LinkFolder: linking is null ? null : linking.LinkAsync)).ConfigureAwait(false),

            // Nothing monitors the site itself. What the reader owns on a site is its scenes, so
            // the monitor slot here marks those rather than the site, and it is null unless the
            // reader asked and every role that pass needs was obtained.
            SyncRegisters.Sites => await SyncLibraryPlanner.RunAsync(
                aimed.Registers,
                new SyncLibrarySource<LibrarySiteIdentity>(
                    runCt => identities.SiteIdentities(aimed.Generation, runCt),
                    site => site.RemoteId,
                    Supplied(aimed.RegisterSite, aimed.Registers),
                    aimed.MonitorSiteScenes),
                progress,
                ct).ConfigureAwait(false),

            _ => throw new InvalidOperationException(
                $"{aimed.Registers} is not a pass this run makes."),
        };

    // The scene pass acts on the identifier inside the row the folder walk yields. A row carrying
    // none never reaches either: the walk skips it, because it is a folder and not a scene.
    //
    // The instance's own id for what it just registered is kept against the identity the offer
    // named, so the folder's files are attached to the entries Cove identified rather than to
    // whatever the instance managed to parse out of their names.
    private static Func<LibrarySceneInFolder, CancellationToken, Task<SyncRegistration>> Registering(
        SyncLibraryAiming aimed, FolderLinking? linking)
    {
        var offer = aimed.RegisterScene
            ?? throw new InvalidOperationException(
                $"A run naming {aimed.Registers} was aimed with no way to register one.");

        return async (row, ct) =>
        {
            var remoteId = Identifier(row);

            // The entry is registered on the root that reaches this folder's own files. A library
            // spread over several volumes cannot be registered on one of them: a hard link cannot
            // cross a filesystem, so the import would copy the bytes instead.
            var root = row.Folder is { } folder && linking is not null
                ? await linking.RootReachingAsync(folder, ct).ConfigureAwait(false)
                : null;

            var answered = await offer(remoteId, root, ct).ConfigureAwait(false);
            linking?.Registered(remoteId, answered);
            return answered;
        };
    }

    private static Func<LibrarySceneInFolder, SyncRegistration, CancellationToken,
        Task<SceneMonitorTally>>? Monitoring(SyncLibraryAiming aimed)
        => aimed.Monitor is { } monitor
            ? (row, answered, ct) => monitor(Identifier(row), answered, ct)
            : null;

    private static string Identifier(LibrarySceneInFolder row)
        => row.RemoteId
            ?? throw new InvalidOperationException(
                "A row naming a folder and no scene reached the offer.");

    // A null offer says nothing about a generation, so it is not expressible as an empty library or
    // as a refusal.
    private static Func<TIdentity, CancellationToken, Task<SyncRegistration>> Supplied<TIdentity>(
        Func<TIdentity, CancellationToken, Task<SyncRegistration>>? offer, SyncRegisters registers)
        => offer
            ?? throw new InvalidOperationException(
                $"A run naming {registers} was aimed with no way to register one.");
}
