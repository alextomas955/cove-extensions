using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;
using WhisparrSync.Scene;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    // Composes v3's scene add with the acquisition-suppressing flag set, so the instance is asked
    // to look for nothing.
    internal static async Task<Results<Ok<SceneActionResult>, BadRequest, ForbiddenCode>>
        AddSceneAsync(
            int coveId,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            ILibraryCardIdentityPort sceneCards,
            IServiceScopeFactory scopes,
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
                coveId, known: null, whisparr, sceneCards, scopes, ct)
                .ConfigureAwait(false));
    }

    // Reached by the route once it has checked the tier, and by a batch run that carries no
    // principal of its own. With known supplied the instance is not resolved again, so a selection
    // costs one stored read rather than one per scene.
    private static async Task<SceneActionResult> AddSceneResolvedAsync(
        int coveId,
        MonitoringTarget? known,
        WhisparrAccess whisparr,
        ILibraryCardIdentityPort sceneCards,
        IServiceScopeFactory scopes,
        CancellationToken ct)
    {
        var (_, _, _, log) = whisparr;

        var (ground, refusal) = await GroundSceneVerbAsync<IWhisparrMissingSceneActing>(
            coveId, known, whisparr, sceneCards, ct).ConfigureAwait(false);
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
            () => target.Reads.ReadQualityProfilesAsync(ct),
            target,
            log,
            ct).ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ContainedAsync(
                () => target.Reads.ReadRootFoldersAsync(ct),
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
                ground.Resolved.RemoteId, composeWith, ct),
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
            WhisparrAccess whisparr,
            ILibraryCardIdentityPort sceneCards,
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
                monitored: true, coveId, known: null, whisparr, sceneCards, ct)
                .ConfigureAwait(false));
    }

    // Governs what the instance takes from here on and retracts nothing already downloaded.
    internal static async Task<Results<Ok<SceneActionResult>, BadRequest, ForbiddenCode>>
        UnmonitorSceneAsync(
            int coveId,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            ILibraryCardIdentityPort sceneCards,
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
                monitored: false, coveId, known: null, whisparr, sceneCards, ct)
                .ConfigureAwait(false));
    }

    // A scene the list already names is answered as taken rather than refused, because one control
    // carries both labels and the label a reader sees has to agree with the instance.
    // Governs what a later catalogue addition takes and retracts nothing already downloaded.
    internal static async Task<Results<Ok<SceneActionResult>, BadRequest, ForbiddenCode>>
        ExcludeSceneAsync(
            int coveId,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            ILibraryCardIdentityPort sceneCards,
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
                coveId, known: null, whisparr, sceneCards, ct)
                .ConfigureAwait(false));
    }

    private static async Task<SceneActionResult> ExcludeSceneResolvedAsync(
        int coveId,
        MonitoringTarget? known,
        WhisparrAccess whisparr,
        ILibraryCardIdentityPort sceneCards,
        CancellationToken ct)
    {
        var (_, _, _, log) = whisparr;

        var (ground, refusal) = await GroundExclusionVerbAsync(
            coveId, known, whisparr, sceneCards, ct).ConfigureAwait(false);
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
            WhisparrAccess whisparr,
            ILibraryCardIdentityPort sceneCards,
            CancellationToken ct)
    {
        var (_, _, _, log) = whisparr;

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
            coveId, known: null, whisparr, sceneCards, ct)
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
            WhisparrAccess whisparr,
            ILibraryCardIdentityPort sceneCards,
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
                coveId, known: null, whisparr, sceneCards, ct)
                .ConfigureAwait(false));
    }

    private static async Task<SceneActionResult> SearchSceneResolvedAsync(
        int coveId,
        MonitoringTarget? known,
        WhisparrAccess whisparr,
        ILibraryCardIdentityPort sceneCards,
        CancellationToken ct)
    {
        var (_, _, _, log) = whisparr;

        var (ground, refusal) = await GroundSceneVerbAsync<IWhisparrSceneSearchGrabbing>(
            coveId, known, whisparr, sceneCards, ct).ConfigureAwait(false);
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
                sceneId, ct),
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
            () => target.Reads.ReadCommandAsync(commandId, ct),
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
}
