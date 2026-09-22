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
            IWhisparrInstanceFactory instances,
            ILibraryCardIdentityPort sceneCards,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sceneCards);

        if ((known
                ?? await ResolveTargetAsync(options, credentials, instances, ct).ConfigureAwait(false))
            is not { } target)
        {
            return (null, SceneRefusalKind.NoInstanceConnected);
        }

        var identity = await sceneCards.ResolveOneAsync(coveId, target.Binding.Generation, ct)
            .ConfigureAwait(false);
        if (identity.RemoteId is not { } remoteId)
        {
            return (null, identity.Refusal);
        }

        if (!IsBoundedSceneId(remoteId))
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
                resolved.RemoteId, ct),
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
        IWhisparrInstanceFactory instances,
        ILibraryCardIdentityPort sceneCards,
        ILogger log,
        CancellationToken ct)
    {
        var (ground, refusal) = await GroundSceneVerbAsync<IWhisparrSceneMonitorActing>(
            coveId, known, options, credentials, instances, sceneCards, log, ct).ConfigureAwait(false);
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
                sceneId, monitored, ct),
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
            IWhisparrInstanceFactory instances,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
        where TActing : class
    {
        var (resolved, refusal) = await ResolveSceneVerbTargetAsync(
            coveId, known, options, credentials, instances, sceneCards, ct).ConfigureAwait(false);
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
            IWhisparrInstanceFactory instances,
            ILibraryCardIdentityPort sceneCards,
            ILogger log,
            CancellationToken ct)
    {
        var (resolved, refusal) = await ResolveSceneVerbTargetAsync(
            coveId, known, options, credentials, instances, sceneCards, ct).ConfigureAwait(false);
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
                resolved.RemoteId, ct)
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
                resolved.Target.Binding.Generation,
                WhisparrSyncLog.Classify(failure),
                resolved.Target.Binding.BaseAddress.Host);
            return SceneExclusionLookup.DidNotComplete;
        }
    }
}
