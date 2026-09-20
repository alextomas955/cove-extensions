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
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Options;
using WhisparrSync.Scene;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapMissingCardEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The configure tier for the same reason as the bulk route: one scene is not a lesser act
        // than a selection of them. Which scene a request touches is a route segment, so a caller
        // cannot name one in a body the route would otherwise have to refuse.
        endpoints.MapPost(MissingSceneMonitorRoute,
            (string kind, int coveId, string providerSceneId, ICurrentPrincipalAccessor principal,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             IServiceScopeFactory scopes, CancellationToken ct)
                => MonitorMissingSceneAsync(
                    kind, coveId, providerSceneId, principal, options, credentials, client, scopes,
                    _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // The configure tier for two reasons rather than one: the route aims this extension's stored
        // credential at a third party AND it spends the reader's indexer traffic and disk. It is the
        // most consequential route this surface mounts, and it must not sit at a tier a caller who
        // cannot configure the extension can reach.
        endpoints.MapPost(MissingSceneSearchRoute,
            (string kind, int coveId, string providerSceneId, ICurrentPrincipalAccessor principal,
             OptionsStore options, ICredentialPort credentials, IWhisparrClient client,
             CancellationToken ct)
                => SearchMissingSceneAsync(
                    kind, coveId, providerSceneId, principal, options, credentials, client, _log, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    /// <summary>The longest provider identifier this product will put in an outbound body.</summary>
    /// <remarks>
    /// The identifier is the one value on this surface a caller names, so it is bounded before it
    /// reaches a request. Both providers issue identifiers far inside this: one a UUID, the other a
    /// decimal integer.
    /// </remarks>
    private const int ProviderSceneIdBound = 128;

    /// <summary>Marks one catalogue scene as wanted, without acquiring it.</summary>
    /// <remarks>
    /// Composes v3's scene add, whose acquisition-suppressing flag is set from the
    /// one constant every non-grabbing body reads. The instance is asked to look for nothing.
    /// </remarks>
    internal static async Task<Results<Ok<MissingSceneActionResult>, BadRequest, ForbiddenCode>>
        MonitorMissingSceneAsync(
            string kind,
            int coveId,
            string providerSceneId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
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

        if (!TryReadEntity(kind, coveId, out var owning) || !IsBoundedSceneId(providerSceneId))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        // A generation registering no scene add has no implementation to hand over, so there is
        // nothing to compose and nothing was sent.
        if (target.Capabilities.Obtain<IWhisparrMissingSceneActing>()
                .Match<IWhisparrMissingSceneActing?>(held => held, _ => null)
            is not { } acting)
        {
            return TypedResults.Ok(
                NothingWasSent(MissingSceneActionRefusal.CapabilityAbsentOnThisGeneration));
        }

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
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        var defaults = AddDefaultsProjector.From(profiles.Body, roots.Body);
        if (defaults.Defaults is not { } runWide)
        {
            return TypedResults.Ok(NothingWasSent(ActionRefusalFor(defaults.Refusal)));
        }

        var composed = await EntityRootThrough(scopes, target, FilesOfEntity(owning, coveId))(
            runWide, ct).ConfigureAwait(false);
        if (composed.Defaults is not { } composeWith)
        {
            return TypedResults.Ok(NothingWasSent(ActionRefusalFor(composed.Refusal)));
        }

        var added = await ContainedAsync(
            () => acting.AddSceneAsync(
                target.BaseAddress, target.ApiKey, providerSceneId, composeWith, ct),
            target,
            log,
            ct).ConfigureAwait(false);

        if (added is null)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        return TypedResults.Ok(
            MonitoringProjector.Accepted(added) is MonitorRefusalKind.None
                ? new MissingSceneActionResult(
                    MissingSceneState.Monitored, MissingSceneActionRefusal.None)
                : NothingWasSent(MissingSceneActionRefusal.InstanceRefused));
    }

    /// <summary>Asks the connected instance to look for one catalogue scene.</summary>
    /// <remarks>
    /// The one verb on this surface that can make an instance download.
    /// <para>
    /// The command names the instance's own identifier for the scene and the browser holds only the
    /// provider's, so the scene is read first. That read is also what tells an instance holding no
    /// entry for the scene apart from one that declined: the first is a legitimate answer and the
    /// state it reports is the one the card goes back to.
    /// </para>
    /// <para>
    /// The command is read back off the instance by its own identifier before the verb reports
    /// anything, so a success status alone never stands as the evidence. One read and no loop, and
    /// nothing here says a release was taken.
    /// </para>
    /// </remarks>
    internal static async Task<Results<Ok<MissingSceneActionResult>, BadRequest, ForbiddenCode>>
        SearchMissingSceneAsync(
            string kind,
            int coveId,
            string providerSceneId,
            ICurrentPrincipalAccessor principal,
            OptionsStore options,
            ICredentialPort credentials,
            IWhisparrClient client,
            ILogger log,
            CancellationToken ct)
    {
        // Checked in the handler, because the route's own declaration enforces nothing on a minimal
        // API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadEntity(kind, coveId, out _) || !IsBoundedSceneId(providerSceneId))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(options, credentials, client, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        // A generation registering neither role has no implementation to hand over, so there is
        // nothing to compose and nothing was sent.
        if (target.Capabilities.Obtain<IWhisparrSceneStatusReading>()
                .Match<IWhisparrSceneStatusReading?>(held => held, _ => null) is not { } reading
            || target.Capabilities.Obtain<IWhisparrSceneSearchGrabbing>()
                .Match<IWhisparrSceneSearchGrabbing?>(held => held, _ => null) is not { } searching)
        {
            return TypedResults.Ok(
                NothingWasSent(MissingSceneActionRefusal.CapabilityAbsentOnThisGeneration));
        }

        var answered = await ContainedAsync(
            () => reading.ReadSceneByRemoteIdAsync(
                target.BaseAddress, target.ApiKey, providerSceneId, ct),
            target,
            log,
            ct).ConfigureAwait(false);
        if (answered is null)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        var scene = SceneStatusPort.ReadRow(answered);
        if (scene.State == MissingSceneState.NotAdded)
        {
            return TypedResults.Ok(
                new MissingSceneActionResult(
                    MissingSceneState.NotAdded, MissingSceneActionRefusal.WhisparrHasNoEntryForScene));
        }

        if (scene.State == MissingSceneState.StatusUnknown)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        if (scene.InstanceId is not { } sceneId)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.InstanceRefused));
        }

        var searched = await ContainedAsync(
            () => searching.SearchSceneAsync(target.BaseAddress, target.ApiKey, sceneId, ct),
            target,
            log,
            ct).ConfigureAwait(false);
        if (searched is null)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        // A body naming no command that can be read is the instance declining, not an accepted
        // search: nothing was named that could be asked about afterwards.
        if (MonitoringProjector.Accepted(searched) is not MonitorRefusalKind.None
            || CommandProjector.IdIn(searched) is not { } commandId)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.InstanceRefused));
        }

        var readBack = await ContainedAsync(
            () => target.Reads.ReadCommandAsync(target.BaseAddress, target.ApiKey, commandId, ct),
            target,
            log,
            ct).ConfigureAwait(false);
        if (readBack is null)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        // The state read off the scene's own row. A search changes what the instance is looking for
        // and not what it holds.
        return TypedResults.Ok(
            CommandProjector.Confirmed(readBack, commandId)
                ? new MissingSceneActionResult(scene.State, MissingSceneActionRefusal.None)
                : NothingWasSent(MissingSceneActionRefusal.InstanceRefused));
    }

    /// <summary>An answer claiming nothing about the instance, and the reason it claims nothing.</summary>
    private static MissingSceneActionResult NothingWasSent(MissingSceneActionRefusal refusal)
        => new(MissingSceneState.StatusUnknown, refusal);

    /// <summary>The card's own vocabulary for a value an add could not be composed without.</summary>
    private static MissingSceneActionRefusal ActionRefusalFor(MonitorRefusalKind refusal)
        => refusal switch
        {
            MonitorRefusalKind.NoQualityProfile
                => MissingSceneActionRefusal.InstanceOffersNoQualityProfile,
            MonitorRefusalKind.NoRootFolder
                => MissingSceneActionRefusal.InstanceOffersNoRootFolder,
            MonitorRefusalKind.NoAgreedRootForThisEntity
                => MissingSceneActionRefusal.NoAgreedRootForThisEntity,
            _ => MissingSceneActionRefusal.InstanceRefused,
        };

    /// <summary>
    /// Whether <paramref name="providerSceneId"/> is inside what this product will send on.
    /// </summary>
    /// <remarks>
    /// A control character would reach a JSON body and a header-shaped answer's parser, and neither
    /// provider issues one, so the bound is on the shape rather than on either provider's spelling.
    /// </remarks>
    private static bool IsBoundedSceneId(string providerSceneId)
        => !string.IsNullOrWhiteSpace(providerSceneId)
            && providerSceneId.Length <= ProviderSceneIdBound
            && !providerSceneId.Any(char.IsControl)
            && providerSceneId.Trim().Length == providerSceneId.Length;
}
