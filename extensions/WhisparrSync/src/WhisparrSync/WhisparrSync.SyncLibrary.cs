using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
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
    /// <summary>The job id one library sync run is minted onto.</summary>
    /// <remarks>
    /// Declared beside the routes rather than on the run, because the count route derives whether a
    /// run is in flight from it and the run reads it for its own type. One literal, so the route's
    /// derivation and the type the host enqueues cannot drift apart.
    /// </remarks>
    internal const string SyncLibraryJobId = "sync-library";

    /// <summary>Starts one count of what a library sync would offer.</summary>
    /// <remarks>
    /// The count is a background run and its id is answered immediately: the comparison is one
    /// request per batch to a third party, and waiting would hold the browser open for its length.
    /// <para>
    /// Enqueued non-exclusive. The count creates nothing and changes nothing, so two of them cost
    /// only requests, and it must not queue behind an unrelated run this extension made exclusive.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<SyncEnqueued>, ForbiddenCode>> EnqueueSyncPreviewAsync(
        ICurrentPrincipalAccessor principal,
        IJobService jobs,
        IServiceScopeFactory scopes,
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrClient client,
        CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(jobs);

        var refused = await SyncRefusalFor(options, credentials, client, ct).ConfigureAwait(false);
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

    /// <summary>Answers the counts the last count left, and whether a run is in flight.</summary>
    /// <remarks>
    /// A local read of the held slot. It starts no run and issues no outbound request, so a reader
    /// opening the settings page has paid nothing.
    /// <para>
    /// Whether a run is in flight is derived from the host's own job list rather than from a stored
    /// flag, so it answers false the moment the run ends and no client-side timer is involved.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<SyncPreviewRead>, ForbiddenCode>> ReadSyncPreviewAsync(
        ICurrentPrincipalAccessor principal,
        IJobService jobs,
        SyncPreviewCache counts,
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrClient client,
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
        var refused = await SyncRefusalFor(options, credentials, client, ct).ConfigureAwait(false);
        if (refused is not SyncRefusalKind.None)
        {
            return TypedResults.Ok(new SyncPreviewRead(null, refused, running));
        }

        var stored = await options.LoadAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(
            new SyncPreviewRead(
                counts.Held(stored.SelectedGeneration), SyncRefusalKind.None, running));
    }

    /// <summary>Runs one enqueued count.</summary>
    /// <remarks>
    /// Everything the count compares against is resolved when it starts, because which instance is
    /// connected is a setting a person can change while a run is queued.
    /// <para>
    /// The summary is the last progress call. The host writes its own unit line over
    /// <c>JobInfo.Summary</c> for a run that declares units, and this one declares none, so the line
    /// written here is the line a reader sees.
    /// </para>
    /// </remarks>
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
                    services.GetRequiredService<IWhisparrClient>(),
                    runCt)
                .ConfigureAwait(false);

            if (target is null)
            {
                return null;
            }

            return SyncPassFor(target) switch
            {
                SyncRegisters.Scenes => new SyncPreviewAiming(
                    target.Generation,
                    SyncRegisters.Scenes,
                    (asked, batchCt) => target.Capabilities
                        .Obtain<IWhisparrSceneStatusReading>()
                        .Match(
                            reads => reads.ReduceHeldScenesAsync(
                                target.BaseAddress, target.ApiKey, asked, batchCt),
                            _ => throw new InvalidOperationException(
                                "A scene count reached a target holding no scene-status read.")),
                    HeldSites: null),

                SyncRegisters.Sites => new SyncPreviewAiming(
                    target.Generation,
                    SyncRegisters.Sites,
                    Held: null,
                    (asked, batchCt) => ReduceHeldSitesAsync(
                        services.GetRequiredService<ISiteNumberPort>(),
                        (numbers, numbersCt) => target.Capabilities
                            .Obtain<IWhisparrHeldSiteReading>()
                            .Match(
                                reads => reads.ReduceHeldSitesAsync(
                                    target.BaseAddress, target.ApiKey, numbers, numbersCt),
                                _ => throw new InvalidOperationException(
                                    "A site count reached a target holding no held-site read.")),
                        asked,
                        batchCt)),

                _ => null,
            };
        }
    }

    /// <summary>
    /// Which of <paramref name="asked"/> the instance holds a site for, and which of them the
    /// metadata source names no site for.
    /// </summary>
    /// <remarks>
    /// The library's identifiers are mapped forward to the numbers a site is named by rather than
    /// the instance's own numbers being mapped back, because a run has to map forward to register
    /// anything at all, and because the reverse direction cannot tell a studio the metadata source
    /// names no site for from one the instance simply does not hold.
    /// <para>
    /// The resolve runs here rather than behind the batched read, which answers a local question and
    /// sends one request: a resolve inside it would cost that read a request per element.
    /// </para>
    /// </remarks>
    /// <exception cref="HttpRequestException">
    /// The metadata source was not reached for one of the identifiers, so nothing about that studio
    /// is known and no count is held. Raised rather than counted as a studio the instance does not
    /// hold, which would offer it for registration on the strength of nothing.
    /// </exception>
    internal static async Task<SiteBatchReading> ReduceHeldSitesAsync(
        ISiteNumberPort siteNumbers,
        Func<IReadOnlyCollection<int>, CancellationToken, Task<IReadOnlySet<int>>> heldSites,
        IReadOnlyCollection<string> asked,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(siteNumbers);
        ArgumentNullException.ThrowIfNull(heldSites);
        ArgumentNullException.ThrowIfNull(asked);

        var numbered = new List<(string Identity, int Number)>(asked.Count);
        var namesNone = new HashSet<string>(StringComparer.Ordinal);

        foreach (var identity in asked)
        {
            var resolved = await siteNumbers.ResolveSiteNumberAsync(identity, ct).ConfigureAwait(false);
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
    }

    /// <summary>Starts one library run, or refuses it by name.</summary>
    /// <remarks>
    /// Enqueued non-exclusive, which is the one departure from every other enqueue in this
    /// extension. A library-wide run takes as long as the library is large, and enqueued exclusive it
    /// would hold the reader's own Cove scans and refreshes behind it for that whole time. A
    /// non-exclusive run still appears in the host's job list and is still cancellable, so nothing a
    /// reader can see or do about it is given up.
    /// <para>
    /// A second run is refused while the first is pending or running, and the refusal carries the
    /// running job's own id so the page can point at it. Whether one is in flight is the host's own
    /// job list rather than a stored flag: it answers false the moment the run ends, and it is empty
    /// after a process restart, which is the correct answer.
    /// </para>
    /// </remarks>
    internal async Task<Results<Ok<SyncEnqueued>, ForbiddenCode>> EnqueueSyncRunAsync(
        SyncRunRequest? request,
        ICurrentPrincipalAccessor principal,
        IJobService jobs,
        IServiceScopeFactory scopes,
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrClient client,
        CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(jobs);

        var refused = await SyncRefusalFor(options, credentials, client, ct).ConfigureAwait(false);
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
            $"[{Name}] Offer every identified scene to Whisparr",
            (progress, runCt) => RunSyncLibraryAsync(parameters, scopes, progress, runCt),
            exclusive: false);

        return TypedResults.Ok(new SyncEnqueued(started, SyncRefusalKind.None));
    }

    /// <summary>Runs one enqueued library run.</summary>
    /// <remarks>
    /// Everything the run acts through is resolved when it starts, because the profile, the root and
    /// which instance is connected are each the reader's to change while a run is queued.
    /// <para>
    /// A cancellation is rethrown after the run has written its own summary, so the host classifies
    /// the run as cancelled rather than completed while the reader is still told what it offered.
    /// </para>
    /// </remarks>
    private async Task RunSyncLibraryAsync(
        IReadOnlyDictionary<string, string> parameters,
        IServiceScopeFactory scopes,
        CoreJobProgress progress,
        CancellationToken ct)
    {
        await SyncLibraryJob.RunAsync(
            SyncLibraryJob.Decode(parameters), scopes, AimAsync, progress, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        async Task<SyncLibraryAiming?> AimAsync(
            IServiceProvider services, SyncLibraryBatch batch, CancellationToken runCt)
        {
            var target = await ResolveTargetAsync(
                    services.GetRequiredService<OptionsStore>(),
                    services.GetRequiredService<ICredentialPort>(),
                    services.GetRequiredService<IWhisparrClient>(),
                    runCt)
                .ConfigureAwait(false);

            if (target is null)
            {
                return null;
            }

            return SyncPassFor(target) switch
            {
                // The one composition every registering run in this product aims through, so the add
                // this run offers is the non-grabbing one and the values it composes with are read
                // here rather than at enqueue.
                //
                // This pass offers scenes and nothing else: the generation that keeps them creates a
                // scene's studio and its performers itself as presence, so there is no studio pass
                // and no performer pass. Nor is there a catalogue refresh after the loop - that is a
                // per-entity act, and there is no single entity here.
                SyncRegisters.Scenes =>
                    await ComposeSceneAddAsync(services, runCt).ConfigureAwait(false) is { } register
                        ? new SyncLibraryAiming(
                            target.Generation,
                            SyncRegisters.Scenes,
                            (identity, sceneCt) => OfferSceneAsync(register, identity, sceneCt),
                            RegisterSite: null,
                            MonitorFor(batch, target))
                        : null,

                // The other pass registers a site's presence. Nothing monitors the site itself: what
                // the reader owns on a site is its scenes, so the monitor slot here marks those.
                SyncRegisters.Sites =>
                    await ComposeSiteRegistrationAsync(services, runCt).ConfigureAwait(false)
                        is { } registerSite
                        ? new SyncLibraryAiming(
                            target.Generation,
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

    /// <summary>Offers one scene and classifies what the instance answered.</summary>
    /// <remarks>
    /// The add's own refusal is what tells a scene the instance already holds from one it declines,
    /// so the classification is composed where that answer arrives.
    /// </remarks>
    private static async Task<SyncRegistration> OfferSceneAsync(
        Func<string, CancellationToken, Task<WhisparrResponse?>> register,
        string identity,
        CancellationToken ct)
        => SyncRegistration.Offered(await register(identity, ct).ConfigureAwait(false));

    /// <summary>
    /// What a site pass registers each site through, or null where it must not act at all.
    /// </summary>
    /// <remarks>
    /// The presence-only add, so nothing this run registers is monitored and nothing it registers is
    /// searched for. The profile and the root are read here rather than at enqueue, for the reason
    /// the scene composition reads them here.
    /// </remarks>
    private async Task<Func<LibrarySiteIdentity, CancellationToken, Task<SyncRegistration>>?>
        ComposeSiteRegistrationAsync(IServiceProvider services, CancellationToken runCt)
    {
        if (await ResolveTargetAsync(
                services.GetRequiredService<OptionsStore>(),
                services.GetRequiredService<ICredentialPort>(),
                services.GetRequiredService<IWhisparrClient>(),
                runCt).ConfigureAwait(false) is not { } target
            || target.Capabilities.Obtain<IWhisparrSiteRegistrationActing>()
                .Match<IWhisparrSiteRegistrationActing?>(held => held, _ => null) is not { } acting
            || target.Capabilities.Obtain<IWhisparrStudioActing>()
                .Match<IWhisparrStudioActing?>(held => held, _ => null) is not { } studios)
        {
            return null;
        }

        var profiles = await ContainedAsync(
            () => target.Reads.ReadQualityProfilesAsync(target.BaseAddress, target.ApiKey, runCt),
            target,
            _log,
            runCt).ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ContainedAsync(
                () => target.Reads.ReadRootFoldersAsync(target.BaseAddress, target.ApiKey, runCt),
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

        return (site, siteCt) => SiteRegistrationStep.RegisterAsync(
            (identity, readCt) => ContainedAsync(
                () => studios.ReadStudioAsync(
                    target.BaseAddress, target.ApiKey, target.Generation, identity, readCt),
                target,
                _log,
                readCt),
            (identity, addCt) => ContainedAsync(
                () => acting.RegisterSiteAsync(
                    target.BaseAddress, target.ApiKey, identity, composeWith, addCt),
                target,
                _log,
                addCt),
            site,
            siteCt);
    }

    /// <summary>
    /// How the scenes a reader owns on one registered site are marked wanted, or null where nothing
    /// marks them.
    /// </summary>
    /// <remarks>
    /// Null unless the reader asked and the generation registers both the row read and the per-scene
    /// monitor. With it null the run registers its sites and makes no provider read and no row read
    /// at all, so the whole cost of monitoring is paid only where it was asked for.
    /// <para>
    /// Whether the connected provider issues a number to address a scene by is not asked here. The
    /// generation this pass runs on reads through the provider that issues one, so a refusal on that
    /// ground would be a sentence no run can produce; a scene left without a number is counted
    /// unnumbered in the run's own ending instead.
    /// </para>
    /// <para>
    /// Every role is obtained by name and none is chosen by comparing a version. The scene stream is
    /// resolved out of the run's own elevated services, because Cove's per-principal query filters
    /// answer an anonymous reader with zero rows and no error - which here would monitor nothing
    /// while reporting a library that holds nothing.
    /// </para>
    /// </remarks>
    private Func<LibrarySiteIdentity, SyncRegistration, CancellationToken, Task<SceneMonitorTally>>?
        ComposeSiteSceneMonitor(
            IServiceProvider services, SyncLibraryBatch batch, MonitoringTarget target)
    {
        if (!batch.AlsoMonitor
            || target.Capabilities.Obtain<IWhisparrSceneMonitorActing>()
                .Match<IWhisparrSceneMonitorActing?>(held => held, _ => null) is not { } monitoring
            || target.Capabilities.Obtain<IWhisparrSiteSceneReading>()
                .Match<IWhisparrSiteSceneReading?>(held => held, _ => null) is not { } rows)
        {
            return null;
        }

        var catalogue = services.GetRequiredService<IProviderCatalogue>();
        var scenes = services.GetRequiredService<IEntitySceneIdentityPort>();

        var ports = new SiteSceneMonitorPorts(
            (studioId, ct) => scenes.SceneIdentitiesFor(
                WhisparrEntityKind.Studio, studioId, target.Generation, ct),
            catalogue.ResolveNumericSceneIdAsync,
            (siteId, numbers, ct) => rows.ReduceSiteSceneRowsAsync(
                target.BaseAddress, target.ApiKey, siteId, numbers, ct),
            (rowId, ct) => ContainedAsync(
                () => monitoring.SetSceneMonitoredAsync(
                    target.BaseAddress, target.ApiKey, target.Generation, rowId, monitored: true, ct),
                target,
                _log,
                ct));

        // A site the instance named no id for is a site nothing can reach the scenes under. Its own
        // registration is already counted as refused, and no scene under it is claimed either way.
        return (site, registered, ct) => registered.InstanceId is { } siteId
            ? SiteSceneMonitorPass.MonitorAsync(ports, site, siteId, _log, ct)
            : Task.FromResult(SceneMonitorTally.Nothing);
    }

    /// <summary>How one offered scene is marked wanted, or null where nothing marks one.</summary>
    /// <remarks>
    /// Null unless the reader asked and the generation registers a per-scene monitor, so the older
    /// generation obtains none and monitors nothing rather than being refused once it is called.
    /// <para>
    /// One request at a time throughout. The instance's own command queue is the shared resource, so
    /// there is no parallel loop here and no second request in flight.
    /// </para>
    /// </remarks>
    private Func<string, SyncRegistration, CancellationToken, Task<SceneMonitorTally>>? MonitorFor(
        SyncLibraryBatch batch, MonitoringTarget target)
    {
        if (!batch.AlsoMonitor
            || target.Capabilities.Obtain<IWhisparrSceneMonitorActing>()
                .Match<IWhisparrSceneMonitorActing?>(held => held, _ => null) is not { } monitoring)
        {
            return null;
        }

        var reading = target.Capabilities.Obtain<IWhisparrSceneStatusReading>()
            .Match<IWhisparrSceneStatusReading?>(held => held, _ => null);

        // One scene, so the tally this pass answers is that one scene either way.
        return async (identity, offered, ct) => SceneMonitorTally.For(
            await MonitorOfferedSceneAsync(target, monitoring, reading, identity, offered, ct)
                .ConfigureAwait(false));
    }

    /// <summary>Marks one scene the instance now holds wanted.</summary>
    /// <remarks>
    /// The flag is set by the instance's own numeric scene id, which is not an identifier this
    /// product holds. It is taken off the accepted add's own answer where the add was accepted, and
    /// otherwise off one read of the scene the instance already held - one extra request for a scene
    /// that was already there, and none for a scene that was just registered.
    /// </remarks>
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
                () => reading.ReadSceneByRemoteIdAsync(target.BaseAddress, target.ApiKey, identity, ct),
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
                    target.BaseAddress,
                    target.ApiKey,
                    target.Generation,
                    named,
                    monitored: true,
                    ct),
                target,
                _log,
                ct).ConfigureAwait(false)
            : null;
    }

    /// <summary>Whether one of this extension's own sync runs is pending or running.</summary>
    private bool SyncRunIsInFlight(IJobService jobs) => SyncRunInFlight(jobs) is not null;

    /// <summary>The one of this extension's own sync runs that is pending or running, or none.</summary>
    /// <remarks>
    /// One derivation for both readers: the count route answers whether a run is in flight, and the
    /// run route names the job it refuses a second run for, so neither can disagree with the other
    /// about what is running.
    /// </remarks>
    private JobInfo? SyncRunInFlight(IJobService jobs)
        => jobs.GetAllJobs().FirstOrDefault(job =>
            string.Equals(
                job.Type, OwnJobTypePrefix + SyncLibraryJobId, StringComparison.Ordinal)
            && job.Status is JobStatus.Pending or JobStatus.Running);

    /// <summary>Why the sync surface cannot act at all, or that it can.</summary>
    /// <remarks>
    /// Both routes refuse for the same two reasons, and a reader is shown one sentence for each, so
    /// the two are derived once rather than in each handler.
    /// </remarks>
    private static async Task<SyncRefusalKind> SyncRefusalFor(
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrClient client,
        CancellationToken ct)
    {
        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return SyncRefusalKind.NoInstanceConnected;
        }

        // Read off the roles the target obtains rather than off its version. A generation keeping no
        // per-scene records registers no scene-status read, so there is nothing to ask which scenes
        // it holds - and what it can be told about instead is its sites. The refusal stands only
        // where neither role is obtained.
        if (SyncPassFor(target) is not null)
        {
            return SyncRefusalKind.None;
        }

        return SyncRefusalKind.WhisparrKeepsNoSceneRecords;
    }

    /// <summary>Which pass <paramref name="target"/> can take, or none.</summary>
    /// <remarks>
    /// One derivation for the three routes and for the run, so the refusal a reader is shown and the
    /// pass the run then makes cannot disagree. Scenes are preferred where both are obtainable: a
    /// per-scene entry is what the reader's own library holds, and a site entry stands in for one
    /// only where no per-scene entry exists.
    /// </remarks>
    private static SyncRegisters? SyncPassFor(MonitoringTarget target)
    {
        if (target.Capabilities.Obtain<IWhisparrSceneStatusReading>()
            .Match<IWhisparrSceneStatusReading?>(held => held, _ => null) is not null)
        {
            return SyncRegisters.Scenes;
        }

        return target.Capabilities.Obtain<IWhisparrSiteRegistrationActing>()
            .Match<IWhisparrSiteRegistrationActing?>(held => held, _ => null) is not null
                ? SyncRegisters.Sites
                : null;
    }
}
