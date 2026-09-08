using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
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
    /// <summary>What the connected instance holds for one scene the library names.</summary>
    /// <remarks>
    /// Reads and changes nothing, and reaches no metadata provider: the identifier the scene is
    /// known by is the library's own stored row.
    /// <para>
    /// The identity is resolved before anything leaves, so a video the library names no single
    /// identifier for costs no request.
    /// </para>
    /// <para>
    /// Nothing is cached. Both reads happen on every open, so what the tab states is what the
    /// instance holds at that moment.
    /// </para>
    /// </remarks>
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

        // A generation registering no per-scene read has no implementation to hand over, so there is
        // nothing to compose and nothing was sent.
        if (target.Capabilities.Obtain<IWhisparrSceneStatusReading>()
                .Match<IWhisparrSceneStatusReading?>(held => held, _ => null) is not { } reading)
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

        // A profile read that answers nothing is not a failed tab: the scene's own facts stand, and
        // the two the profile carries are reported as unestablished.
        var profiles = await ContainedAsync(
            () => target.Reads.ReadQualityProfilesAsync(target.BaseAddress, target.ApiKey, ct),
            target,
            log,
            ct).ConfigureAwait(false);

        return TypedResults.Ok(SceneDetailProjector.Project(answered, profiles));
    }

    /// <summary>Adds one scene the connected instance does not hold.</summary>
    /// <remarks>
    /// Composes the newer generation's scene add, whose acquisition-suppressing flag is set from the
    /// one constant every non-grabbing body reads. The instance is asked to look for nothing.
    /// <para>
    /// The instance's own row is read first, so a scene it already holds is answered rather than
    /// added a second time, and both values an add cannot be composed without are read off the
    /// instance before anything is sent.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<SceneActionResult>, BadRequest, ForbiddenCode>>
        AddSceneAsync(
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

        var (ground, refusal) = await GroundSceneVerbAsync<IWhisparrMissingSceneActing>(
            coveId, options, credentials, client, sceneCards, log, ct).ConfigureAwait(false);
        if (ground is null)
        {
            return TypedResults.Ok(ActionRefused(refusal));
        }

        if (ground.Row.State != MissingSceneState.NotAdded)
        {
            return TypedResults.Ok(ActionRefused(SceneRefusalKind.WhisparrAlreadyHoldsThisScene));
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
            return TypedResults.Ok(ActionRefused(SceneRefusalKind.DidNotReachWhisparr));
        }

        // The indexer list is not read and no verb is refused for it: an absent indexer is the
        // instance's own business, and these two are the settings that stop an add before anything
        // is sent.
        var defaults = AddDefaultsProjector.From(profiles.Body, roots.Body);
        if (defaults.Defaults is not { } composeWith)
        {
            return TypedResults.Ok(ActionRefused(SceneRefusalFor(defaults.Refusal)));
        }

        var added = await ContainedAsync(
            () => ground.Acting.AddSceneAsync(
                target.BaseAddress, target.ApiKey, ground.Resolved.RemoteId, composeWith, ct),
            target,
            log,
            ct).ConfigureAwait(false);

        return TypedResults.Ok(Classified(added));
    }

    /// <summary>Asks the connected instance to want one scene it holds.</summary>
    /// <remarks>
    /// Sets the monitored flag and nothing else. The instance is asked to look for nothing now: what
    /// a monitored scene takes later is the instance's own schedule.
    /// </remarks>
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

        return await SetSceneMonitoringAsync(
            monitored: true, coveId, options, credentials, client, sceneCards, log, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Asks the connected instance to stop wanting one scene it holds.</summary>
    /// <remarks>
    /// Governs what the instance takes from here on and retracts nothing already downloaded.
    /// </remarks>
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

        return await SetSceneMonitoringAsync(
            monitored: false, coveId, options, credentials, client, sceneCards, log, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Puts one scene on the connected instance's own exclusion list.</summary>
    /// <remarks>
    /// A scene the list already names is answered as taken rather than refused, because one control
    /// carries both labels and the label a reader sees has to agree with the instance.
    /// <para>
    /// Governs what a later catalogue addition takes and retracts nothing already downloaded.
    /// </para>
    /// </remarks>
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

        var (ground, refusal) = await GroundExclusionVerbAsync(
            coveId, options, credentials, client, sceneCards, log, ct).ConfigureAwait(false);
        if (ground is null)
        {
            return TypedResults.Ok(ActionRefused(refusal));
        }

        if (ground.Held.ExclusionId is not null)
        {
            return TypedResults.Ok(Took());
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

        return TypedResults.Ok(Classified(excluded));
    }

    /// <summary>Takes one scene back off the connected instance's own exclusion list.</summary>
    /// <remarks>
    /// The removing route addresses the exclusion by the exclusion row's own identifier, so the list
    /// is read first. A list naming no exclusion for the scene is the instance stating an absence,
    /// and nothing is sent.
    /// </remarks>
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
            coveId, options, credentials, client, sceneCards, log, ct).ConfigureAwait(false);
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

    /// <summary>Asks the connected instance to look for one scene it holds and monitors.</summary>
    /// <remarks>
    /// The one verb on this surface that can make an instance download. The read decides whether it
    /// is sent at all: a scene the instance holds no entry for, or holds and is not monitoring, would
    /// have nothing found for it, so each answers its own refusal and sends nothing.
    /// <para>
    /// What the answer claims is that the instance holds the command, read back off the instance by
    /// the command's own identifier. One read and no loop: nothing here waits for a download, reads a
    /// queue, or says a release was taken.
    /// </para>
    /// </remarks>
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

        var (ground, refusal) = await GroundSceneVerbAsync<IWhisparrSceneSearchGrabbing>(
            coveId, options, credentials, client, sceneCards, log, ct).ConfigureAwait(false);
        if (ground is null)
        {
            return TypedResults.Ok(ActionRefused(refusal));
        }

        if (ground.Row.State == MissingSceneState.NotAdded)
        {
            return TypedResults.Ok(ActionRefused(SceneRefusalKind.WhisparrHasNoEntryForScene));
        }

        if (ground.Row.State == MissingSceneState.Unmonitored)
        {
            return TypedResults.Ok(
                ActionRefused(SceneRefusalKind.WhisparrIsNotMonitoringThisScene));
        }

        if (ground.Row.InstanceId is not { } sceneId)
        {
            return TypedResults.Ok(ActionRefused(SceneRefusalKind.InstanceRefused));
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
            return TypedResults.Ok(ActionRefused(SceneRefusalKind.DidNotReachWhisparr));
        }

        // A body naming no command that can be read is the instance declining, not an accepted
        // search: nothing was named that could be asked about afterwards.
        if (MonitoringProjector.Accepted(posted) is not MonitorRefusalKind.None
            || CommandProjector.IdIn(posted) is not { } commandId)
        {
            return TypedResults.Ok(ActionRefused(SceneRefusalKind.InstanceRefused));
        }

        var readBack = await ContainedAsync(
            () => target.Reads.ReadCommandAsync(target.BaseAddress, target.ApiKey, commandId, ct),
            target,
            log,
            ct).ConfigureAwait(false);
        if (readBack is null)
        {
            return TypedResults.Ok(ActionRefused(SceneRefusalKind.DidNotReachWhisparr));
        }

        return TypedResults.Ok(
            CommandProjector.Confirmed(readBack, commandId)
                ? new SceneActionResult(SceneRefusalKind.None, SearchIsWithWhisparr: true)
                : ActionRefused(SceneRefusalKind.InstanceRefused));
    }

    /// <summary>The instance and the identifier to name on it, once both are resolved.</summary>
    private sealed record SceneVerbTarget(MonitoringTarget Target, string RemoteId);

    /// <summary>What a scene verb needs before it can act, once the read has answered.</summary>
    private sealed record SceneGround<TActing>(
        SceneVerbTarget Resolved, TActing Acting, SceneOnInstance Row);

    /// <summary>What either exclusion half needs before it can act.</summary>
    private sealed record SceneExclusionGround(
        SceneVerbTarget Resolved, IWhisparrSceneExclusionActing Acting, SceneExclusionLookup Held);

    /// <summary>An answer claiming nothing about the instance, and the reason it claims nothing.</summary>
    private static SceneDetailView NothingWasSent(SceneRefusalKind refusal)
        => new(refusal, false, null, null, null, null, null, false);

    /// <summary>A verb that did not take, and the reason it did not.</summary>
    private static SceneActionResult ActionRefused(SceneRefusalKind refusal) => new(refusal, false);

    /// <summary>A verb that took, claiming nothing beyond that.</summary>
    private static SceneActionResult Took() => new(SceneRefusalKind.None, false);

    /// <summary>What the instance's own answer to a non-grabbing verb reads back as.</summary>
    /// <remarks>
    /// A null answer is a request that produced none. What the instance then holds is read by the
    /// browser's own re-read rather than claimed here.
    /// </remarks>
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

    /// <summary>The scene surface's own vocabulary for a value an add could not be composed without.</summary>
    private static SceneRefusalKind SceneRefusalFor(MonitorRefusalKind refusal)
        => refusal switch
        {
            MonitorRefusalKind.NoQualityProfile
                => SceneRefusalKind.InstanceOffersNoQualityProfile,
            MonitorRefusalKind.NoRootFolder => SceneRefusalKind.InstanceOffersNoRootFolder,
            _ => SceneRefusalKind.InstanceRefused,
        };

    /// <summary>The instance to act against and the scene's identifier on it, or the refusal.</summary>
    /// <remarks>
    /// The identity is resolved server-side off the library's own stored row, so no scene identifier
    /// a browser supplied reaches a request, and it is bounded here because this is the last point
    /// before one could.
    /// </remarks>
    private static async Task<(SceneVerbTarget? Resolved, SceneRefusalKind Refusal)>
        ResolveSceneVerbTargetAsync(
            int coveId,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sceneCards);

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
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

    /// <summary>The instance's own row for the scene, or the refusal a verb stops at.</summary>
    /// <remarks>
    /// A row the read established nothing about is held apart from an absence: the first claims
    /// nothing about the instance and the second is the instance reporting that it holds no entry.
    /// </remarks>
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

    /// <summary>Sets the monitored flag on one scene the instance already holds.</summary>
    /// <remarks>
    /// The read decides whether the verb is sent at all: a scene the instance holds no entry for has
    /// no instance-side identifier for the field-scoped patch to name.
    /// </remarks>
    private static async Task<Results<Ok<SceneActionResult>, BadRequest, ForbiddenCode>>
        SetSceneMonitoringAsync(
            bool monitored,
            int coveId,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
    {
        if (coveId < 1)
        {
            return TypedResults.BadRequest();
        }

        var (ground, refusal) = await GroundSceneVerbAsync<IWhisparrSceneMonitorActing>(
            coveId, options, credentials, client, sceneCards, log, ct).ConfigureAwait(false);
        if (ground is null)
        {
            return TypedResults.Ok(ActionRefused(refusal));
        }

        if (ground.Row.State == MissingSceneState.NotAdded)
        {
            return TypedResults.Ok(ActionRefused(SceneRefusalKind.WhisparrHasNoEntryForScene));
        }

        if (ground.Row.InstanceId is not { } sceneId)
        {
            return TypedResults.Ok(ActionRefused(SceneRefusalKind.InstanceRefused));
        }

        var target = ground.Resolved.Target;
        var flipped = await ContainedAsync(
            () => ground.Acting.SetSceneMonitoredAsync(
                target.BaseAddress, target.ApiKey, sceneId, monitored, ct),
            target,
            log,
            ct).ConfigureAwait(false);

        return TypedResults.Ok(Classified(flipped));
    }

    /// <summary>
    /// What a verb acting on one scene the instance holds needs, or the refusal it stops at.
    /// </summary>
    /// <remarks>
    /// The order is the one the whole surface shares: the instance, then the identity, then the
    /// roles, then the instance's own row. A generation registering either role none has nothing to
    /// compose, so no request is made and the answer names the absence rather than the instance.
    /// </remarks>
    private static async Task<(SceneGround<TActing>? Ground, SceneRefusalKind Refusal)>
        GroundSceneVerbAsync<TActing>(
            int coveId,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
        where TActing : class
    {
        var (resolved, refusal) = await ResolveSceneVerbTargetAsync(
            coveId, options, credentials, client, sceneCards, ct).ConfigureAwait(false);
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

    /// <summary>
    /// What either exclusion half needs, or the refusal it stops at.
    /// </summary>
    /// <remarks>
    /// The list is read before either half acts. The removing route addresses the exclusion row's
    /// own identifier, and the excluding half answers a scene the list already names as taken, so
    /// both need what the list says before anything is sent.
    /// <para>
    /// No scene read happens here. An exclusion governs what a later catalogue addition takes, and
    /// whether the instance holds the scene now does not bear on that.
    /// </para>
    /// </remarks>
    private static async Task<(SceneExclusionGround? Ground, SceneRefusalKind Refusal)>
        GroundExclusionVerbAsync(
            int coveId,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
    {
        var (resolved, refusal) = await ResolveSceneVerbTargetAsync(
            coveId, options, credentials, client, sceneCards, ct).ConfigureAwait(false);
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

    /// <summary>What the instance's exclusion list says about the scene.</summary>
    /// <remarks>
    /// Contained the way every other request off this surface is: a read that raised produced no
    /// whole answer, which is not the same as a list naming no exclusion.
    /// </remarks>
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
