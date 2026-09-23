using System.Collections.Concurrent;
using System.Globalization;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Addressing;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Import;
using WhisparrSync.Jobs;
using WhisparrSync.Library;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Providers;
using WhisparrSync.Scene;
using WhisparrSync.Whisparr;
using CoreJobProgress = Cove.Core.Interfaces.IJobProgress;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapSyncLibraryEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The configure tier, because this aims the stored credential at a third party and reads
        // the whole library to do it.
        endpoints.MapPost(SyncPreviewRoute,
            (ICurrentPrincipalAccessor principal, IJobService jobs, IServiceScopeFactory scopes,
             OptionsStore options, ICredentialPort credentials, IWhisparrInstanceFactory instances,
             CancellationToken ct)
                => EnqueueSyncPreviewAsync(
                    principal, jobs, scopes, options, credentials, instances, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The same tier for the read half: it reports how much of the reader's library a third
        // party holds, which is the same fact whichever route answered it.
        endpoints.MapGet(SyncPreviewRoute,
            (ICurrentPrincipalAccessor principal, IJobService jobs, SyncPreviewCache counts,
             OptionsStore options, ICredentialPort credentials, IWhisparrInstanceFactory instances,
             CancellationToken ct)
                => ReadSyncPreviewAsync(
                    principal, jobs, counts, options, credentials, instances, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The same tier again: this one writes into a third party's catalogue on behalf of the
        // whole library.
        endpoints.MapPost(SyncRunRoute,
            (SyncRunRequest? request, ICurrentPrincipalAccessor principal, IJobService jobs,
             IServiceScopeFactory scopes, OptionsStore options, ICredentialPort credentials,
             IWhisparrInstanceFactory instances, CancellationToken ct)
                => EnqueueSyncRunAsync(
                    request, principal, jobs, scopes, options, credentials, instances, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // One literal, so the route's in-flight derivation and the type the host enqueues cannot drift
    // apart.
    internal const string SyncLibraryJobId = "sync-library";

    // Enqueued non-exclusive. The count creates nothing and changes nothing, and it must not queue
    // behind an unrelated run this extension made exclusive.
    internal async Task<Results<Ok<SyncEnqueued>, ForbiddenCode>> EnqueueSyncPreviewAsync(
        ICurrentPrincipalAccessor principal,
        IJobService jobs,
        IServiceScopeFactory scopes,
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrInstanceFactory instances,
        CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(jobs);

        var refused = await SyncRefusalFor(options, credentials, instances, ct).ConfigureAwait(false);
        if (refused is not SyncRefusalKind.None)
        {
            return TypedResults.Ok(new SyncEnqueued(null, refused));
        }

        var started = jobs.Enqueue(
            OwnJobTypePrefix + SyncPreviewJob.JobId,
            $"[{Name}] Count what a library sync would offer",
            (progress, runCt) => RunSyncPreviewAsync(scopes, progress, runCt),
            exclusive: false);

        return TypedResults.Ok(new SyncEnqueued(started, SyncRefusalKind.None));
    }

    // Whether a run is in flight is derived from the host's own job list rather than from a stored
    // flag, so it answers false the moment the run ends.
    internal async Task<Results<Ok<SyncPreviewRead>, ForbiddenCode>> ReadSyncPreviewAsync(
        ICurrentPrincipalAccessor principal,
        IJobService jobs,
        SyncPreviewCache counts,
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrInstanceFactory instances,
        CancellationToken ct)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(options);

        var running = SyncRunIsInFlight(jobs);
        var refused = await SyncRefusalFor(options, credentials, instances, ct).ConfigureAwait(false);
        if (refused is not SyncRefusalKind.None)
        {
            return TypedResults.Ok(new SyncPreviewRead(null, refused, running));
        }

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(
            new SyncPreviewRead(
                counts.Held(stored.SelectedGeneration), SyncRefusalKind.None, running));
    }

    // What the count compares against is resolved when the run starts, because the connected
    // instance is a setting a person can change while a run is queued.
    // The summary is the last progress call: the host writes its own unit line over
    // JobInfo.Summary for a run that declares units, and this one declares none.
    private async Task RunSyncPreviewAsync(
        IServiceScopeFactory scopes, CoreJobProgress progress, CancellationToken ct)
    {
        var counted = await SyncPreviewJob.RunAsync(scopes, AimAsync, _log, ct).ConfigureAwait(false);
        if (counted is null)
        {
            return;
        }

        progress.SetSummary(SyncPreviewJob.SummaryOf(counted));
        ct.ThrowIfCancellationRequested();

        async Task<SyncPreviewAiming?> AimAsync(IServiceProvider services, CancellationToken runCt)
        {
            var target = await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrInstanceFactory>(),
                    runCt)
                .ConfigureAwait(false);

            if (target is null)
            {
                return null;
            }

            return SyncPassFor(target) switch
            {
                SyncRegisters.Scenes => new SyncPreviewAiming(
                    target.Binding.Generation,
                    SyncRegisters.Scenes,
                    (asked, batchCt) => target.Reads is IWhisparrSceneStatusReading reads
                        ? reads.ReduceHeldScenesAsync(asked, batchCt)
                        : throw new InvalidOperationException(
                            "A scene count reached a target holding no scene-status read."),
                    HeldSites: null),

                SyncRegisters.Sites => new SyncPreviewAiming(
                    target.Binding.Generation,
                    SyncRegisters.Sites,
                    Held: null,
                    (asked, batchCt) => ReduceHeldSitesAsync(
                        services.GetRequiredService<ISiteNumberPort>(),
                        target.Binding,
                        (numbers, numbersCt) => target.Reads is IWhisparrHeldSiteReading reads
                            ? reads.ReduceHeldSitesAsync(numbers, numbersCt)
                            : throw new InvalidOperationException(
                                "A site count reached a target holding no held-site read."),
                        asked,
                        batchCt)),

                _ => null,
            };
        }
    }

    // The library's identifiers are mapped forward to the numbers a site is named by, because the
    // reverse direction cannot tell a studio the metadata source names no site for from one the
    // instance does not hold.
    // Throws HttpRequestException where the metadata source was not reached for an identifier.
    // Counting it as a studio the instance does not hold would offer it for registration on the
    // strength of nothing.
    internal static async Task<SiteBatchReading> ReduceHeldSitesAsync(
        ISiteNumberPort siteNumbers,
        WhisparrBinding binding,
        Func<IReadOnlyCollection<int>, CancellationToken, Task<IReadOnlySet<int>>> heldSites,
        IReadOnlyCollection<string> asked,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(siteNumbers);
        ArgumentNullException.ThrowIfNull(heldSites);
        ArgumentNullException.ThrowIfNull(asked);

        var numbered = new List<(string Identity, int Number)>(asked.Count);
        var namesNone = new HashSet<string>(StringComparer.Ordinal);

        using var outstanding = new SemaphoreSlim(SyncPreviewJob.MetadataResolvesInFlight);
        var resolutions = await Task.WhenAll(asked.Select(ResolveAsync)).ConfigureAwait(false);

        foreach (var (identity, resolved) in resolutions)
        {
            if (!resolved.WasReached)
            {
                throw new HttpRequestException(
                    "The metadata source was not reached for a studio the library holds, so which "
                        + "sites the instance is missing was not established.");
            }

            if (resolved.Number is { } named)
            {
                numbered.Add((identity, named));
            }
            else
            {
                namesNone.Add(identity);
            }
        }

        var held = await heldSites(
                [.. numbered.Select(pair => pair.Number).Distinct()], ct)
            .ConfigureAwait(false);

        return new SiteBatchReading(
            numbered.Where(pair => held.Contains(pair.Number))
                .Select(pair => pair.Identity)
                .ToHashSet(StringComparer.Ordinal),
            namesNone);

        async Task<(string Identity, WhisparrSiteNumber Resolved)> ResolveAsync(string identity)
        {
            // The wait is here rather than around the request, so the resolves past the bound queue
            // on this semaphore instead of on the instance's own, which refuses a caller past its
            // queue depth rather than holding it.
            await outstanding.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return (
                    identity,
                    await siteNumbers.ResolveSiteNumberAsync(binding, identity, ct)
                        .ConfigureAwait(false));
            }
            finally
            {
                outstanding.Release();
            }
        }
    }

    // Enqueued non-exclusive. A library-wide run takes as long as the library is large, and
    // exclusive it would hold the reader's own Cove scans and refreshes behind it for that time.
    // A second run is refused while the first is pending or running. The host's job list is the
    // source: it answers false the moment a run ends and is empty after a process restart.
    internal async Task<Results<Ok<SyncEnqueued>, ForbiddenCode>> EnqueueSyncRunAsync(
        SyncRunRequest? request,
        ICurrentPrincipalAccessor principal,
        IJobService jobs,
        IServiceScopeFactory scopes,
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrInstanceFactory instances,
        CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(jobs);

        var refused = await SyncRefusalFor(options, credentials, instances, ct).ConfigureAwait(false);
        if (refused is not SyncRefusalKind.None)
        {
            return TypedResults.Ok(new SyncEnqueued(null, refused));
        }

        if (SyncRunInFlight(jobs) is { } running)
        {
            return TypedResults.Ok(new SyncEnqueued(running.Id, SyncRefusalKind.AlreadyRunning));
        }

        var parameters = SyncLibraryJob.Encode(request?.AlsoMonitor ?? false);

        var started = jobs.Enqueue(
            OwnJobTypePrefix + SyncLibraryJob.JobId,
            $"[{Name}] Offer every identified entry to Whisparr",
            (progress, runCt) => RunSyncLibraryAsync(parameters, scopes, progress, runCt),
            exclusive: false);

        return TypedResults.Ok(new SyncEnqueued(started, SyncRefusalKind.None));
    }

    // Everything the run acts through is resolved when it starts, because the profile, the root
    // and the connected instance are each the reader's to change while a run is queued.
    // A cancellation is rethrown after the run has written its summary, so the host classifies the
    // run as cancelled while the reader is still told what it offered.
    private async Task RunSyncLibraryAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        // One entry per library root the pass asked about, so the settings page can offer a root
        // this run could not settle.
        var readings = new ConcurrentDictionary<string, AddressedFolder>(StringComparer.Ordinal);

        // The generation the pass aimed at, not the one selected when it ends: a selection change
        // during a pass would otherwise file what this instance established under the other one.
        WhisparrGeneration? aimedAt = null;

        await SyncLibraryJob.RunAsync(
            SyncLibraryJob.Decode(parameters), scopes, AimAsync, progress, ct).ConfigureAwait(false);

        // Before the cancellation check, for the reason RecordRootReadingsAsync states: what a run
        // established about a root holds whether or not the run went on to finish.
        if (aimedAt is { } generation)
        {
            await RecordRootReadingsAsync(
                scopes,
                generation,
                [.. readings.Values
                    .Where(reading => reading.Refusal is not null)
                    .Select(reading => new FolderAddressRefusal(
                        reading.CoveRoot, reading.Refusal!.Value, reading.Tried))],
                [.. readings.Values
                    .Where(reading => reading.Refusal is null)
                    .Select(reading => reading.CoveRoot)])
                .ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        async Task<SyncLibraryAiming?> AimAsync(
            IServiceProvider services, SyncLibraryBatch batch, CancellationToken runCt)
        {
            var target = await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrInstanceFactory>(),
                    runCt)
                .ConfigureAwait(false);

            if (target is null)
            {
                return null;
            }

            aimedAt = target.Binding.Generation;

            return SyncPassFor(target) switch
            {
                // This pass offers scenes and nothing else: the generation that keeps them creates
                // a scene's studio and its performers itself, so there is no studio pass and no
                // performer pass. There is no catalogue refresh after the loop either, because
                // that is a per-entity act and there is no single entity here.
                SyncRegisters.Scenes =>
                    await ComposeSceneAddAsync(owningKind: null, owningId: 0, services, runCt)
                            .ConfigureAwait(false) is { } register
                        ? new SyncLibraryAiming(
                            target.Binding.Generation,
                            SyncRegisters.Scenes,
                            (identity, sceneCt) => OfferSceneAsync(register, identity, sceneCt),
                            RegisterSite: null,
                            MonitorFor(batch, target))
                        : null,

                // Nothing monitors the site itself: what the reader owns on a site is its scenes,
                // so the monitor slot here marks those.
                SyncRegisters.Sites =>
                    await ComposeSiteRegistrationAsync(services, readings, runCt)
                            .ConfigureAwait(false)
                        is { } registerSite
                        ? new SyncLibraryAiming(
                            target.Binding.Generation,
                            SyncRegisters.Sites,
                            RegisterScene: null,
                            registerSite,
                            Monitor: null,
                            ComposeSiteSceneMonitor(services, batch, target))
                        : null,

                _ => null,
            };
        }
    }

    // The add's own refusal is what tells a scene the instance already holds from one it declines.
    private static async Task<SyncRegistration> OfferSceneAsync(
        Func<string, CancellationToken, Task<WhisparrResponse?>> register,
        string identity,
        CancellationToken ct)
        => SyncRegistration.Offered(await register(identity, ct).ConfigureAwait(false));

    // The presence-only add, so nothing this run registers is monitored or searched for.
    private async Task<Func<LibrarySiteIdentity, CancellationToken, Task<SyncRegistration>>?>
        ComposeSiteRegistrationAsync(
            IServiceProvider services,
            ConcurrentDictionary<string, AddressedFolder> readings,
            CancellationToken runCt)
    {
        if (await ResolveTargetAsync(
                services.GetRequiredService<OptionsStore>(),
                services.GetRequiredService<ICredentialPort>(),
                services.GetRequiredService<IWhisparrInstanceFactory>(),
                runCt).ConfigureAwait(false) is not { } target
            || target.Reads is not IWhisparrSiteRegistrationActing acting
            || target.Reads is not IWhisparrStudioActing studios)
        {
            return null;
        }

        var profiles = await ContainedAsync(
            () => target.Reads.ReadQualityProfilesAsync(runCt),
            target,
            _log,
            runCt).ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ContainedAsync(
                () => target.Reads.ReadRootFoldersAsync(runCt),
                target,
                _log,
                runCt).ConfigureAwait(false);
        if (profiles is null || roots is null)
        {
            return null;
        }

        if (AddDefaultsProjector.From(profiles.Body, roots.Body).Defaults is not { } composeWith)
        {
            return null;
        }

        // Resolved out of the run's own elevated services. Cove's per-principal query filters
        // answer an anonymous reader zero rows and no error, which here would report every studio
        // as owning no file and register all of them at the run-wide root.
        var files = services.GetRequiredService<IEntityFolderPort>();
        var library = services.GetRequiredService<ICoveLibraryPort>();
        var addressing = AgreedRootThrough(
            target, services.GetRequiredService<IFolderAddressPort>());
        var agreedRoot = async (string coveRoot, CancellationToken addressCt) =>
        {
            var addressed = await addressing(coveRoot, addressCt).ConfigureAwait(false);
            readings[addressed.CoveRoot] = addressed;
            return addressed;
        };

        return async (site, siteCt) =>
        {
            var composed = await EntityAddDefaults.ComposeAsync(
                composeWith,
                library.LibraryRoots,
                (coveRoot, countCt) => files.FilesUnderAsync(
                    WhisparrEntityKind.Studio, site.StudioId, coveRoot, countCt),
                agreedRoot,
                siteCt).ConfigureAwait(false);

            // A composition that refused still reaches the step, so the site is read. Where the
            // instance already holds it, its root need not be settled for its scenes to be marked,
            // and stopping short would leave a whole run's scenes unflagged.
            var registered = await SiteRegistrationStep.RegisterAsync(
                (identity, readCt) => ContainedAsync(
                    () => studios.ReadStudioAsync(
                        identity, readCt),
                    target,
                    _log,
                    readCt),
                (identity, addCt) => composed.Defaults is { } addWith
                    ? ContainedAsync(
                        () => acting.RegisterSiteAsync(
                            identity, addWith, addCt),
                        target,
                        _log,
                        addCt)
                    : Task.FromResult<WhisparrResponse?>(Nothing(composed.Refusal)),
                (siteId, agreed, moveCt) => ContainedAsync(
                    () => acting.MoveSiteRootAsync(
                        siteId, agreed, moveCt),
                    target,
                    _log,
                    moveCt),
                (siteId, refreshCt) => ContainedAsync(
                    () => acting.RefreshSiteCatalogueAsync(
                        siteId, refreshCt),
                    target,
                    _log,
                    refreshCt),
                composed.Root.InstanceRoot,
                site,
                siteCt).ConfigureAwait(false);

            if (registered.Registration is SceneRegistration.Refused)
            {
                WhisparrSyncLog.SiteRegistrationRefused(
                    _log, site.StudioId, site.RemoteId, RefusalReason(registered.Answer));
            }

            return registered with { Root = composed.Root };
        };

        // Nothing was sent, so there is no status to classify and the refusal is all a caller reads.
        static WhisparrResponse Nothing(MonitorRefusalKind refusal)
            => new(0, null, string.Empty) { Refusal = refusal };

        static string RefusalReason(WhisparrResponse? answer)
        {
            if (answer is null)
            {
                return "nothing arrived";
            }

            return answer.Refusal is not MonitorRefusalKind.None
                ? answer.Refusal.ToString()
                : "status " + answer.StatusCode.ToString(CultureInfo.InvariantCulture);
        }
    }

    // A generation this product cannot ask refuses the root rather than composing one: a root
    // nobody checked reads back as a clean pass over an entry holding nothing.
    private static Func<string, CancellationToken, Task<AddressedFolder>> AgreedRootThrough(
        MonitoringTarget target, IFolderAddressPort addressing)
    {
        if (FilesystemReadingOn(target) is not { } role)
        {
            return (_, _) => Task.FromResult(
                new AddressedFolder(
                    null,
                    FolderAgreementRefusal.InstanceCannotBeAsked,
                    string.Empty,
                    Array.Empty<string>()));
        }

        var aimed = new FolderAddressTarget(target.Binding, role);

        return (coveRoot, ct) => addressing.AgreedRootAsync(aimed, coveRoot, ct);
    }

    // Null unless the reader asked and the generation registers both the row read and the
    // per-scene monitor, so the cost of monitoring is paid only where it was asked for.
    // The scene stream is resolved out of the run's own elevated services. Cove's per-principal
    // query filters answer an anonymous reader zero rows and no error, which here would monitor
    // nothing while reporting a library that holds nothing.
    private Func<LibrarySiteIdentity, SyncRegistration, CancellationToken, Task<SceneMonitorTally>>?
        ComposeSiteSceneMonitor(
            IServiceProvider services, SyncLibraryBatch batch, MonitoringTarget target)
    {
        if (!batch.AlsoMonitor
            || target.Reads is not IWhisparrSceneMonitorActing monitoring
            || target.Reads is not IWhisparrSiteSceneReading rows)
        {
            return null;
        }

        var catalogues = services.GetRequiredService<ProviderCatalogueSource>();
        var scenes = services.GetRequiredService<IEntitySceneIdentityPort>();

        var ports = new SiteSceneMonitorPorts(
            (studioId, ct) => scenes.SceneIdentitiesFor(
                WhisparrEntityKind.Studio, studioId, target.Binding.Generation, ct),
            async (providerSceneId, ct) =>
                await (await catalogues(ct).ConfigureAwait(false))
                    .ResolveNumericSceneIdAsync(providerSceneId, ct)
                    .ConfigureAwait(false),
            (siteId, numbers, ct) => rows.ReduceSiteSceneRowsAsync(
                siteId, numbers, ct),
            (rowId, ct) => ContainedAsync(
                () => monitoring.SetSceneMonitoredAsync(
                    rowId, monitored: true, ct),
                target,
                _log,
                ct));

        // A site the instance named no id for is a site nothing can reach the scenes under. Its
        // registration is already counted as refused.
        return (site, registered, ct) => registered.InstanceId is { } siteId
            ? SiteSceneMonitorPass.MonitorAsync(ports, site, siteId, _log, ct)
            : Task.FromResult(SceneMonitorTally.Nothing);
    }

    // Null unless the reader asked and the generation registers a per-scene monitor, so v2 obtains
    // none and monitors nothing rather than being refused once it is called.
    // One request at a time: the instance's own command queue is the shared resource.
    private Func<string, SyncRegistration, CancellationToken, Task<SceneMonitorTally>>? MonitorFor(
        SyncLibraryBatch batch, MonitoringTarget target)
    {
        if (!batch.AlsoMonitor
            || target.Reads is not IWhisparrSceneMonitorActing monitoring)
        {
            return null;
        }

        var reading = target.Reads as IWhisparrSceneStatusReading;

        return async (identity, offered, ct) => SceneMonitorTally.For(
            await MonitorOfferedSceneAsync(target, monitoring, reading, identity, offered, ct)
                .ConfigureAwait(false));
    }

    // The flag is set by the instance's own numeric scene id, which this product does not hold. It
    // is taken off an accepted add's answer, and otherwise off one read of the scene the instance
    // already held.
    private async Task<WhisparrResponse?> MonitorOfferedSceneAsync(
        MonitoringTarget target,
        IWhisparrSceneMonitorActing monitoring,
        IWhisparrSceneStatusReading? reading,
        string identity,
        SyncRegistration offered,
        CancellationToken ct)
    {
        var sceneId = offered.InstanceId;

        if (sceneId is null && reading is not null)
        {
            var held = await ContainedAsync(
                () => reading.ReadSceneByRemoteIdAsync(identity, ct),
                target,
                _log,
                ct).ConfigureAwait(false);
            sceneId = held is null ? null : SceneStatusPort.ReadRow(held).InstanceId;
        }

        // No id is no flag to set. Answering nothing counts it as not monitored, which is the
        // reading that claims less rather than more.
        return sceneId is { } named
            ? await ContainedAsync(
                () => monitoring.SetSceneMonitoredAsync(
                    named,
                    monitored: true,
                    ct),
                target,
                _log,
                ct).ConfigureAwait(false)
            : null;
    }

    private bool SyncRunIsInFlight(IJobService jobs) => SyncRunInFlight(jobs) is not null;

    private JobInfo? SyncRunInFlight(IJobService jobs)
        => jobs.GetAllJobs().FirstOrDefault(job =>
            string.Equals(
                job.Type, OwnJobTypePrefix + SyncLibraryJobId, StringComparison.Ordinal)
            && job.Status is JobStatus.Pending or JobStatus.Running);

    private static async Task<SyncRefusalKind> SyncRefusalFor(
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrInstanceFactory instances,
        CancellationToken ct)
    {
        if (await ResolveTargetAsync(options, credentials, instances, ct).ConfigureAwait(false)
            is not { } target)
        {
            return SyncRefusalKind.NoInstanceConnected;
        }

        // Read off the roles the target obtains rather than off its version. A generation keeping
        // no per-scene records registers no scene-status read, and can be told about its sites
        // instead, so the refusal stands only where neither role is obtained.
        if (SyncPassFor(target) is not null)
        {
            return SyncRefusalKind.None;
        }

        return SyncRefusalKind.WhisparrKeepsNoSceneRecords;
    }

    // Scenes are preferred where both are obtainable: a per-scene entry is what the reader's own
    // library holds, and a site entry stands in only where no per-scene entry exists.
    private static SyncRegisters? SyncPassFor(MonitoringTarget target)
    {
        if (target.Reads is IWhisparrSceneStatusReading)
        {
            return SyncRegisters.Scenes;
        }

        return target.Reads is IWhisparrSiteRegistrationActing
                ? SyncRegisters.Sites
                : null;
    }
}
