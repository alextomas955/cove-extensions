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
             ICredentialPort credentials, IWhisparrInstanceFactory instances,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => SceneDetailAsync(
                    coveId, principal, options, credentials, instances, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        // The configure tier for each of the five: each aims the stored credential at a third party
        // and creates or removes items in the reader's own Whisparr. Which scene a request touches
        // is a path segment, so a caller cannot name one in a body.
        endpoints.MapPost(SceneAddRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrInstanceFactory instances,
             ILibraryCardIdentityPort sceneCards, IServiceScopeFactory scopes,
             CancellationToken ct)
                => AddSceneAsync(
                    coveId, principal, options, credentials, instances, sceneCards, scopes, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SceneMonitorRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrInstanceFactory instances,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => MonitorSceneAsync(
                    coveId, principal, options, credentials, instances, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SceneUnmonitorRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrInstanceFactory instances,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => UnmonitorSceneAsync(
                    coveId, principal, options, credentials, instances, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SceneExcludeRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrInstanceFactory instances,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => ExcludeSceneAsync(
                    coveId, principal, options, credentials, instances, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        endpoints.MapPost(SceneRemoveExclusionRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrInstanceFactory instances,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => RemoveSceneExclusionAsync(
                    coveId, principal, options, credentials, instances, sceneCards, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The configure tier for two reasons: the route aims the stored credential at a third party
        // and it spends the reader's indexer traffic and disk.
        endpoints.MapPost(SceneSearchRoute,
            (int coveId, ICurrentPrincipalAccessor principal, OptionsStore options,
             ICredentialPort credentials, IWhisparrInstanceFactory instances,
             ILibraryCardIdentityPort sceneCards, CancellationToken ct)
                => SearchSceneNowAsync(
                    coveId, principal, options, credentials, instances, sceneCards, _log, ct))
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
            IWhisparrInstanceFactory instances,
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

        if (await ResolveTargetAsync(options, credentials, instances, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(NothingWasSent(SceneRefusalKind.NoInstanceConnected));
        }

        var identity = await sceneCards.ResolveOneAsync(coveId, target.Binding.Generation, ct)
            .ConfigureAwait(false);
        if (identity.RemoteId is not { } remoteId)
        {
            return TypedResults.Ok(NothingWasSent(identity.Refusal));
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
                remoteId, ct),
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
            () => target.Reads.ReadQualityProfilesAsync(ct),
            target,
            log,
            ct).ConfigureAwait(false);

        return TypedResults.Ok(
            SceneDetailProjector.Project(
                answered, profiles, excluded: excluded.ExclusionId is not null));
    }
}
