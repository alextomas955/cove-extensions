using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Scene;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapSceneEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The read tier, not the configure one: the route names one scene as a path segment and
        // composes no write.
        endpoints.MapGet(SceneDetailRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => SceneDetailAsync(
                    coveId, principal, options, credentials, client, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        // The configure tier for each of the five: each aims the stored credential at a third party
        // and creates or removes items in the reader's own Whisparr. Which scene a request touches
        // is a path segment, so a caller cannot name one in a body.
        endpoints.MapPost(SceneAddRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, IServiceScopeFactory scopes,
             CancellationToken ct)
                => AddSceneAsync(
                    coveId, principal, options, credentials, client, sceneCards, scopes, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SceneMonitorRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => MonitorSceneAsync(
                    coveId, principal, options, credentials, client, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SceneUnmonitorRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => UnmonitorSceneAsync(
                    coveId, principal, options, credentials, client, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SceneExcludeRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => ExcludeSceneAsync(
                    coveId, principal, options, credentials, client, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SceneRemoveExclusionRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => RemoveSceneExclusionAsync(
                    coveId, principal, options, credentials, client, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The configure tier for two reasons: the route aims the stored credential at a third party
        // and it spends the reader's indexer traffic and disk.
        endpoints.MapPost(SceneSearchRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrClient client,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => SearchSceneNowAsync(
                    coveId, principal, options, credentials, client, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // Nothing is cached, so what the tab states is what the instance held at that moment.
    // The exclusion list is read as well as the scene, because the state vocabulary tests exclusion
    // first: an unread list would let an excluded scene read as monitored, so a list that answered
    // nothing refuses the whole read.
    internal static async Task<Results<Ok<SceneDetailView>, BadRequest, ForbiddenCode>>
        SceneDetailAsync(
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        if (coveId < 1)
        {
            return TypedResults.BadRequest();
        }

        ArgumentNullException.ThrowIfNull(sceneCards);

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(NothingWasSent(SceneRefusalKind.NoInstanceConnected));
        }

        var identities = await sceneCards.ResolveAsync([coveId], target.Generation, ct)
            .ConfigureAwait(false);
        if (identities is not [{ RemoteId: { } remoteId }])
        {
            return TypedResults.Ok(
                NothingWasSent(SceneRefusalKind.NoIdentityInThisNamespace));
        }

        if (target.Capabilities.Obtain<IWhisparrSceneStatusReading>()
                .Match<IWhisparrSceneStatusReading?>(held => held, _ => null) is not { } reading
            || target.Capabilities.Obtain<IWhisparrSceneExclusionReading>()
                .Match<IWhisparrSceneExclusionReading?>(held => held, _ => null)
                is not { } exclusions)
        {
            return TypedResults.Ok(
                NothingWasSent(SceneRefusalKind.CapabilityAbsentOnThisGeneration));
        }

        var answered = await ContainedAsync(
            () => reading.ReadSceneByRemoteIdAsync(
                target.BaseAddress, target.ApiKey, remoteId, ct),
            target,
            log,
            ct).ConfigureAwait(false);
        if (answered is null)
        {
            return TypedResults.Ok(NothingWasSent(SceneRefusalKind.DidNotReachWhisparr));
        }

        var excluded = await FindExclusionAsync(
            exclusions, new SceneVerbTarget(target, remoteId), log, ct).ConfigureAwait(false);
        if (!excluded.ReadCompleted)
        {
            return TypedResults.Ok(NothingWasSent(SceneRefusalKind.DidNotReachWhisparr));
        }

        // A profile read that answers nothing is not a failed tab: the scene's own facts stand.
        var profiles = await ContainedAsync(
            () => target.Reads.ReadQualityProfilesAsync(target.BaseAddress, target.ApiKey, ct),
            target,
            log,
            ct).ConfigureAwait(false);

        return TypedResults.Ok(
            SceneDetailProjector.Project(
                answered, profiles, excluded: excluded.ExclusionId is not null));
    }

    // Composes v3's scene add with the acquisition-suppressing flag set, so the instance is asked
    // to look for nothing.
    internal static async Task<Results<Ok<SceneActionResult>, BadRequest, ForbiddenCode>>
        AddSceneAsync(
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            IServiceScopeFactory scopes,
            ILogger log,
            CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        if (coveId < 1)
        {
            return TypedResults.BadRequest();
        }

        return TypedResults.Ok(
            await AddSceneResolvedAsync(
                coveId, known: null, options, credentials, client, sceneCards, scopes, log, ct)
                .ConfigureAwait(false));
    }

    // Reached by the route once it has checked the tier, and by a batch run that carries no
    // principal of its own. With known supplied the instance is not resolved again, so a selection
    // costs one stored read rather than one per scene.
    private static async Task<SceneActionResult> AddSceneResolvedAsync(
        int coveId,
        MonitoringTarget? known,
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrClient client,
        ILibraryCardIdentityPort sceneCards,
        IServiceScopeFactory scopes,
        ILogger log,
        CancellationToken ct)
    {
        var (ground, refusal) = await GroundSceneVerbAsync<IWhisparrMissingSceneActing>(
            coveId, known, options, credentials, client, sceneCards, log, ct).ConfigureAwait(false);
        if (ground is null)
        {
            return ActionRefused(refusal);
        }

        if (ground.Row.State != MissingSceneState.NotAdded)
        {
            return ActionRefused(SceneRefusalKind.WhisparrAlreadyHoldsThisScene);
        }

        var target = ground.Resolved.Target;
        var profiles = await ContainedAsync(
            () => target.Reads.ReadQualityProfilesAsync(target.BaseAddress, target.ApiKey, ct),
            target,
            log,
            ct).ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ContainedAsync(
                () => target.Reads.ReadRootFoldersAsync(target.BaseAddress, target.ApiKey, ct),
                target,
                log,
                ct).ConfigureAwait(false);
        if (profiles is null || roots is null)
        {
            return ActionRefused(SceneRefusalKind.DidNotReachWhisparr);
        }

        // The indexer list is not read and no verb is refused for it: an absent indexer is the
        // instance's own business.
        var defaults = AddDefaultsProjector.From(profiles.Body, roots.Body);
        if (defaults.Defaults is not { } runWide)
        {
            return ActionRefused(SceneRefusalFor(defaults.Refusal));
        }

        // Counted over this scene's own files rather than its studio's, and as System: a
        // per-principal filter would report a video that holds a file as holding none, and the add
        // would go to the wrong root with no error.
        var composed = await EntityRootThrough(scopes, target, FilesOfVideo(coveId))(runWide, ct)
            .ConfigureAwait(false);
        if (composed.Defaults is not { } composeWith)
        {
            return ActionRefused(SceneRefusalFor(composed.Refusal));
        }

        var added = await ContainedAsync(
            () => ground.Acting.AddSceneAsync(
                target.BaseAddress, target.ApiKey, ground.Resolved.RemoteId, composeWith, ct),
            target,
            log,
            ct).ConfigureAwait(false);

        return Classified(added);
    }

    // Sets the monitored flag and nothing else. The instance is asked to look for nothing now.
    internal static async Task<Results<Ok<SceneActionResult>, BadRequest, ForbiddenCode>>
        MonitorSceneAsync(
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        if (coveId < 1)
        {
            return TypedResults.BadRequest();
        }

        return TypedResults.Ok(
            await SetSceneMonitoringResolvedAsync(
                monitored: true, coveId, known: null, options, credentials, client, sceneCards, log, ct)
                .ConfigureAwait(false));
    }

    // Governs what the instance takes from here on and retracts nothing already downloaded.
    internal static async Task<Results<Ok<SceneActionResult>, BadRequest, ForbiddenCode>>
        UnmonitorSceneAsync(
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        if (coveId < 1)
        {
            return TypedResults.BadRequest();
        }

        return TypedResults.Ok(
            await SetSceneMonitoringResolvedAsync(
                monitored: false, coveId, known: null, options, credentials, client, sceneCards, log, ct)
                .ConfigureAwait(false));
    }

    // A scene the list already names is answered as taken rather than refused, because one control
    // carries both labels and the label a reader sees has to agree with the instance.
    // Governs what a later catalogue addition takes and retracts nothing already downloaded.
    internal static async Task<Results<Ok<SceneActionResult>, BadRequest, ForbiddenCode>>
        ExcludeSceneAsync(
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        if (coveId < 1)
        {
            return TypedResults.BadRequest();
        }

        return TypedResults.Ok(
            await ExcludeSceneResolvedAsync(
                coveId, known: null, options, credentials, client, sceneCards, log, ct)
                .ConfigureAwait(false));
    }

    private static async Task<SceneActionResult> ExcludeSceneResolvedAsync(
        int coveId,
        MonitoringTarget? known,
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrClient client,
        ILibraryCardIdentityPort sceneCards,
        ILogger log,
        CancellationToken ct)
    {
        var (ground, refusal) = await GroundExclusionVerbAsync(
            coveId, known, options, credentials, client, sceneCards, log, ct).ConfigureAwait(false);
        if (ground is null)
        {
            return ActionRefused(refusal);
        }

        if (ground.Held.ExclusionId is not null)
        {
            return Took();
        }

        var excluded = await ContainedAsync(
            () => ground.Acting.AddSceneExclusionAsync(
                ground.Resolved.Target.BaseAddress,
                ground.Resolved.Target.ApiKey,
                ground.Resolved.RemoteId,
                ct),
            ground.Resolved.Target,
            log,
            ct).ConfigureAwait(false);

        return Classified(excluded);
    }

    // The exclusion is addressed by the exclusion row's own identifier, so the list is read first.
    // A list naming no exclusion for the scene is the instance stating an absence.
    internal static async Task<Results<Ok<SceneActionResult>, BadRequest, ForbiddenCode>>
        RemoveSceneExclusionAsync(
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        if (coveId < 1)
        {
            return TypedResults.BadRequest();
        }

        var (ground, refusal) = await GroundExclusionVerbAsync(
            coveId, known: null, options, credentials, client, sceneCards, log, ct)
            .ConfigureAwait(false);
        if (ground is null)
        {
            return TypedResults.Ok(ActionRefused(refusal));
        }

        if (ground.Held.ExclusionId is not { } exclusionId)
        {
            return TypedResults.Ok(ActionRefused(SceneRefusalKind.WhisparrHasNoEntryForScene));
        }

        var removed = await ContainedAsync(
            () => ground.Acting.RemoveSceneExclusionAsync(
                ground.Resolved.Target.BaseAddress,
                ground.Resolved.Target.ApiKey,
                exclusionId,
                ct),
            ground.Resolved.Target,
            log,
            ct).ConfigureAwait(false);

        return TypedResults.Ok(Classified(removed));
    }

    // The one verb on this surface that can make an instance download.
    // The answer claims only that the instance holds the command, read back by the command's own
    // identifier. Nothing here waits for a download, reads a queue, or says a release was taken.
    internal static async Task<Results<Ok<SceneActionResult>, BadRequest, ForbiddenCode>>
        SearchSceneNowAsync(
            int coveId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        if (coveId < 1)
        {
            return TypedResults.BadRequest();
        }

        return TypedResults.Ok(
            await SearchSceneResolvedAsync(
                coveId, known: null, options, credentials, client, sceneCards, log, ct)
                .ConfigureAwait(false));
    }

    private static async Task<SceneActionResult> SearchSceneResolvedAsync(
        int coveId,
        MonitoringTarget? known,
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrClient client,
        ILibraryCardIdentityPort sceneCards,
        ILogger log,
        CancellationToken ct)
    {
        var (ground, refusal) = await GroundSceneVerbAsync<IWhisparrSceneSearchGrabbing>(
            coveId, known, options, credentials, client, sceneCards, log, ct).ConfigureAwait(false);
        if (ground is null)
        {
            return ActionRefused(refusal);
        }

        if (ground.Row.State == MissingSceneState.NotAdded)
        {
            return ActionRefused(SceneRefusalKind.WhisparrHasNoEntryForScene);
        }

        if (ground.Row.State == MissingSceneState.Unmonitored)
        {
            return ActionRefused(SceneRefusalKind.WhisparrIsNotMonitoringThisScene);
        }

        if (ground.Row.InstanceId is not { } sceneId)
        {
            return ActionRefused(SceneRefusalKind.InstanceRefused);
        }

        var target = ground.Resolved.Target;
        var posted = await ContainedAsync(
            () => ground.Acting.SearchSceneAsync(
                target.BaseAddress, target.ApiKey, sceneId, ct),
            target,
            log,
            ct).ConfigureAwait(false);
        if (posted is null)
        {
            return ActionRefused(SceneRefusalKind.DidNotReachWhisparr);
        }

        // A body naming no command that can be read is the instance declining, not an accepted
        // search: nothing was named that could be asked about afterwards.
        if (MonitoringProjector.Accepted(posted) is not MonitorRefusalKind.None
            || CommandProjector.IdIn(posted) is not { } commandId)
        {
            return ActionRefused(SceneRefusalKind.InstanceRefused);
        }

        var readBack = await ContainedAsync(
            () => target.Reads.ReadCommandAsync(target.BaseAddress, target.ApiKey, commandId, ct),
            target,
            log,
            ct).ConfigureAwait(false);
        if (readBack is null)
        {
            return ActionRefused(SceneRefusalKind.DidNotReachWhisparr);
        }

        return CommandProjector.Confirmed(readBack, commandId)
            ? new SceneActionResult(SceneRefusalKind.None, SearchIsWithWhisparr: true)
            : ActionRefused(SceneRefusalKind.InstanceRefused);
    }

    private sealed record SceneVerbTarget(MonitoringTarget Target, string RemoteId);

    private sealed record SceneGround<TActing>(
        SceneVerbTarget Resolved, TActing Acting, SceneOnInstance Row);

    private sealed record SceneExclusionGround(
        SceneVerbTarget Resolved, IWhisparrSceneExclusionActing Acting, SceneExclusionLookup Held);

    private static SceneDetailView NothingWasSent(SceneRefusalKind refusal)
        => new(refusal, false, null, null, null, null, null, false);

    private static SceneActionResult ActionRefused(SceneRefusalKind refusal) => new(refusal, false);

    private static SceneActionResult Took() => new(SceneRefusalKind.None, false);

    // A null answer is a request that produced none. What the instance then holds is read by the
    // browser's own re-read rather than claimed here.
    private static SceneActionResult Classified(WhisparrResponse? answered)
    {
        if (answered is null)
        {
            return ActionRefused(SceneRefusalKind.DidNotReachWhisparr);
        }

        return MonitoringProjector.Accepted(answered) is MonitorRefusalKind.None
            ? Took()
            : ActionRefused(SceneRefusalKind.InstanceRefused);
    }

    private static SceneRefusalKind SceneRefusalFor(MonitorRefusalKind refusal)
        => refusal switch
        {
            MonitorRefusalKind.NoQualityProfile
                => SceneRefusalKind.InstanceOffersNoQualityProfile,
            MonitorRefusalKind.NoRootFolder => SceneRefusalKind.InstanceOffersNoRootFolder,
            MonitorRefusalKind.NoAgreedRootForThisEntity
                => SceneRefusalKind.NoAgreedRootForThisEntity,
            _ => SceneRefusalKind.InstanceRefused,
        };

    // The identity is resolved server-side off the library's own stored row, so no scene identifier
    // a browser supplied reaches a request. It is bounded here, the last point before one could.
    private static async Task<(SceneVerbTarget? Resolved, SceneRefusalKind Refusal)>
        ResolveSceneVerbTargetAsync(
            int coveId,
            MonitoringTarget? known,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sceneCards);

        if ((known
                ?? await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false))
            is not { } target)
        {
            return (null, SceneRefusalKind.NoInstanceConnected);
        }

        var identities = await sceneCards.ResolveAsync([coveId], target.Generation, ct)
            .ConfigureAwait(false);
        if (identities is not [{ RemoteId: { } remoteId }] || !IsBoundedSceneId(remoteId))
        {
            return (null, SceneRefusalKind.NoIdentityInThisNamespace);
        }

        return (new SceneVerbTarget(target, remoteId), SceneRefusalKind.None);
    }

    // A row the read established nothing about is held apart from an absence: the first claims
    // nothing about the instance, the second is the instance reporting that it holds no entry.
    private static async Task<(SceneOnInstance? Row, SceneRefusalKind Refusal)> ReadSceneRowAsync(
        IWhisparrSceneStatusReading reading,
        SceneVerbTarget resolved,
        ILogger log,
        CancellationToken ct)
    {
        var answered = await ContainedAsync(
            () => reading.ReadSceneByRemoteIdAsync(
                resolved.Target.BaseAddress, resolved.Target.ApiKey, resolved.RemoteId, ct),
            resolved.Target,
            log,
            ct).ConfigureAwait(false);
        if (answered is null)
        {
            return (null, SceneRefusalKind.DidNotReachWhisparr);
        }

        var row = SceneStatusPort.ReadRow(answered);
        return row.State == MissingSceneState.StatusUnknown
            ? (null, SceneRefusalKind.DidNotReachWhisparr)
            : (row, SceneRefusalKind.None);
    }

    // The read decides whether the verb is sent at all: a scene the instance holds no entry for
    // has no instance-side identifier for the field-scoped patch to name.
    private static async Task<SceneActionResult> SetSceneMonitoringResolvedAsync(
        bool monitored,
        int coveId,
        MonitoringTarget? known,
        OptionsStore options,
        ICredentialPort credentials,
        IWhisparrClient client,
        ILibraryCardIdentityPort sceneCards,
        ILogger log,
        CancellationToken ct)
    {
        var (ground, refusal) = await GroundSceneVerbAsync<IWhisparrSceneMonitorActing>(
            coveId, known, options, credentials, client, sceneCards, log, ct).ConfigureAwait(false);
        if (ground is null)
        {
            return ActionRefused(refusal);
        }

        if (ground.Row.State == MissingSceneState.NotAdded)
        {
            return ActionRefused(SceneRefusalKind.WhisparrHasNoEntryForScene);
        }

        if (ground.Row.InstanceId is not { } sceneId)
        {
            return ActionRefused(SceneRefusalKind.InstanceRefused);
        }

        var target = ground.Resolved.Target;
        var flipped = await ContainedAsync(
            () => ground.Acting.SetSceneMonitoredAsync(
                target.BaseAddress, target.ApiKey, target.Generation, sceneId, monitored, ct),
            target,
            log,
            ct).ConfigureAwait(false);

        return Classified(flipped);
    }

    // The order the whole surface shares: the instance, then the identity, then the roles, then
    // the instance's own row. A generation registering either role none has nothing to compose, so
    // no request is made and the answer names the absence rather than the instance.
    private static async Task<(SceneGround<TActing>? Ground, SceneRefusalKind Refusal)>
        GroundSceneVerbAsync<TActing>(
            int coveId,
            MonitoringTarget? known,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
        where TActing : class
    {
        var (resolved, refusal) = await ResolveSceneVerbTargetAsync(
            coveId, known, options, credentials, client, sceneCards, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return (null, refusal);
        }

        var capabilities = resolved.Target.Capabilities;
        if (capabilities.Obtain<IWhisparrSceneStatusReading>()
                .Match<IWhisparrSceneStatusReading?>(held => held, _ => null) is not { } reading
            || capabilities.Obtain<TActing>().Match<TActing?>(held => held, _ => null)
                is not { } acting)
        {
            return (null, SceneRefusalKind.CapabilityAbsentOnThisGeneration);
        }

        var (row, readRefusal) = await ReadSceneRowAsync(reading, resolved, log, ct)
            .ConfigureAwait(false);
        return row is null
            ? (null, readRefusal)
            : (new SceneGround<TActing>(resolved, acting, row), SceneRefusalKind.None);
    }

    // No scene read happens here. An exclusion governs what a later catalogue addition takes, and
    // whether the instance holds the scene now does not bear on that.
    private static async Task<(SceneExclusionGround? Ground, SceneRefusalKind Refusal)>
        GroundExclusionVerbAsync(
            int coveId,
            MonitoringTarget? known,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
    {
        var (resolved, refusal) = await ResolveSceneVerbTargetAsync(
            coveId, known, options, credentials, client, sceneCards, ct).ConfigureAwait(false);
        if (resolved is null)
        {
            return (null, refusal);
        }

        var capabilities = resolved.Target.Capabilities;
        if (capabilities.Obtain<IWhisparrSceneExclusionReading>()
                .Match<IWhisparrSceneExclusionReading?>(held => held, _ => null) is not { } reading
            || capabilities.Obtain<IWhisparrSceneExclusionActing>()
                .Match<IWhisparrSceneExclusionActing?>(held => held, _ => null) is not { } acting)
        {
            return (null, SceneRefusalKind.CapabilityAbsentOnThisGeneration);
        }

        var held = await FindExclusionAsync(reading, resolved, log, ct).ConfigureAwait(false);
        return held.ReadCompleted
            ? (new SceneExclusionGround(resolved, acting, held), SceneRefusalKind.None)
            : (null, SceneRefusalKind.DidNotReachWhisparr);
    }

    // Contained the way every other request off this surface is: a read that raised produced no
    // whole answer, which is not the same as a list naming no exclusion.
    private static async Task<SceneExclusionLookup> FindExclusionAsync(
        IWhisparrSceneExclusionReading reading,
        SceneVerbTarget resolved,
        ILogger log,
        CancellationToken ct)
    {
        try
        {
            return await reading.FindSceneExclusionAsync(
                resolved.Target.BaseAddress, resolved.Target.ApiKey, resolved.RemoteId, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
            when (failure is HttpRequestException or IOException or TaskCanceledException)
        {
            WhisparrSyncLog.MonitoringRequestContained(
                log,
                resolved.Target.Generation,
                WhisparrSyncLog.Classify(failure),
                resolved.Target.BaseAddress.Host);
            return SceneExclusionLookup.DidNotComplete;
        }
    }
}
