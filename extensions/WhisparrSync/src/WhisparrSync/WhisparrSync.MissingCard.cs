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
using WhisparrSync.Scene;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapMissingCardEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Configure tier, as the bulk route: one scene is no lesser act than a selection of them.
        endpoints.MapPost(MissingSceneMonitorRoute,
            ([AsParameters] MissingSceneRoute scene, ICurrentPrincipalAccessor principal,
             WhisparrAccess whisparr, IEntityIdentityPort identities, InstanceCatalogueCache cache,
             IServiceScopeFactory scopes, CancellationToken ct)
                => MonitorMissingSceneAsync(
                    scene, principal, whisparr, identities, cache, scopes, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);

        // Configure tier: the route aims the stored credential at a third party and spends the
        // reader's indexer traffic and disk.
        endpoints.MapPost(MissingSceneSearchRoute,
            ([AsParameters] MissingSceneRoute addressed, ICurrentPrincipalAccessor principal,
             WhisparrAccess whisparr, CancellationToken ct)
                => SearchMissingSceneAsync(addressed, principal, whisparr, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // The identifier is the one value on this surface a caller names, so it is bounded before it
    // reaches a request. Both providers issue identifiers far inside this bound: a UUID and a
    // decimal integer.
    private const int ProviderSceneIdBound = 128;

    // Marks one catalogue scene as wanted without acquiring it: v3's scene add, with the
    // acquisition-suppressing flag from the constant every non-grabbing body reads.
    internal static async Task<Results<Ok<MissingSceneActionResult>, BadRequest, ForbiddenCode>>
        MonitorMissingSceneAsync(
            MissingSceneRoute scene,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            IEntityIdentityPort identities,
            InstanceCatalogueCache cache,
            IServiceScopeFactory scopes,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scene);
        var (kind, coveId, providerSceneId) = scene;
        var route = new EntityRoute(kind, coveId);

        var (_, _, _, log) = whisparr;

        // Re-checked here because the route declaration enforces nothing on a minimal API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(cache);

        if (!TryReadEntity(route, out var owning) || !IsBoundedSceneId(providerSceneId))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(whisparr, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        // One generation lists a catalogue it already holds a row for every scene of, so marking one
        // there is a flip of that row. Composing an add would ask it to create what it has.
        if (target.Reads is not IWhisparrMissingSceneActing)
        {
            return await MarkHeldSceneRowAsync(
                new OwnedSceneAddress(owning, coveId, providerSceneId),
                target, identities, cache, log, ct).ConfigureAwait(false);
        }

        // A generation registering no scene add has no implementation to hand over, so there is
        // nothing to compose and nothing was sent.
        if (target.Reads is not IWhisparrMissingSceneActing acting)
        {
            return TypedResults.Ok(
                NothingWasSent(MissingSceneActionRefusal.CapabilityAbsentOnThisGeneration));
        }

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
                providerSceneId, composeWith, ct),
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

    // Marks the row the instance already holds for this scene, for a generation whose catalogue is
    // its own rows rather than a source's listing. The row's id is read from the catalogue this tab
    // was drawn from, held for a short window, so the mark normally costs one request.
    private static async Task<Results<Ok<MissingSceneActionResult>, BadRequest, ForbiddenCode>>
        MarkHeldSceneRowAsync(
            OwnedSceneAddress scene,
            MonitoringTarget target,
            IEntityIdentityPort identities,
            InstanceCatalogueCache cache,
            ILogger log,
            CancellationToken ct)
    {
        var (owning, coveId, providerSceneId) = scene;

        if (target.Reads is not IWhisparrSceneMonitorActing marking
            || target.Reads is not IWhisparrEntityCatalogueReading reading)
        {
            return TypedResults.Ok(
                NothingWasSent(MissingSceneActionRefusal.CapabilityAbsentOnThisGeneration));
        }

        var identity = await identities.ResolveAsync(owning, coveId, target.Binding.Generation, ct)
            .ConfigureAwait(false);
        if (identity.ForeignId is not { Length: > 0 } foreignId)
        {
            return TypedResults.Ok(
                NothingWasSent(MissingSceneActionRefusal.CapabilityAbsentOnThisGeneration));
        }

        var scenes = cache.Held(target.Binding.Generation, owning, foreignId);
        if (scenes is null)
        {
            // The catalogue read answers its own refusals rather than raising, so it is called
            // directly: there is no contained failure for the shared helper to classify.
            var answered = await reading.ReadEntityCatalogueAsync(
                owning, foreignId, ct)
                .ConfigureAwait(false);
            if (answered.Scenes is not { } listed)
            {
                return TypedResults.Ok(
                    NothingWasSent(
                        answered.Refusal == WhisparrCatalogueRefusal.EntityNotHeld
                            ? MissingSceneActionRefusal.WhisparrHasNoEntryForScene
                            : MissingSceneActionRefusal.DidNotReachWhisparr));
            }

            cache.Hold(target.Binding.Generation, owning, foreignId, listed);
            scenes = listed;
        }

        // A scene the catalogue names no row for is one the instance holds no entry for, which is
        // the same answer the other generation gives when its own add finds nothing.
        var row = scenes.FirstOrDefault(
            scene => string.Equals(
                scene.ProviderSceneId, providerSceneId, StringComparison.Ordinal));
        if (row is not { InstanceSceneId: > 0 })
        {
            return TypedResults.Ok(
                NothingWasSent(MissingSceneActionRefusal.WhisparrHasNoEntryForScene));
        }

        // The entity's own flag gates every scene under it on this generation: a release for an
        // unmonitored entity is turned down before the scene's flag is read at all, so marking the
        // scene alone would report a state the instance never acts on. Done first, so a mark that
        // is reported always means something.
        if (await EntityIsMonitoredForAsync(owning, foreignId, target, log, ct)
                .ConfigureAwait(false) is not { } alreadyMonitored)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        if (!alreadyMonitored
            && await MonitorOwningEntityAsync(owning, foreignId, target, log, ct)
                .ConfigureAwait(false) is not true)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.InstanceRefused));
        }

        var marked = await ContainedAsync(
            () => marking.SetSceneMonitoredAsync(
                row.InstanceSceneId,
                monitored: true,
                ct),
            target,
            log,
            ct).ConfigureAwait(false);

        if (marked is null)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        // The flip is a write, so what the instance now holds is its own to state on the next read.
        cache.Forget();

        return TypedResults.Ok(
            MonitoringProjector.Accepted(marked) is MonitorRefusalKind.None
                ? new MissingSceneActionResult(
                    MissingSceneState.Monitored, MissingSceneActionRefusal.None)
                : NothingWasSent(MissingSceneActionRefusal.InstanceRefused));
    }

    // Whether the instance monitors the entity these scenes sit under, or null where it was asked
    // and said nothing.
    private static async Task<bool?> EntityIsMonitoredForAsync(
        WhisparrEntityKind owning,
        string foreignId,
        MonitoringTarget target,
        ILogger log,
        CancellationToken ct)
    {
        if (ReadingEntity(owning, target) is not { } reading)
        {
            return null;
        }

        var answered = await ContainedAsync(() => reading(foreignId, ct), target, log, ct)
            .ConfigureAwait(false);

        return answered is null ? null : MonitoringProjector.MonitoredIn(answered.Body);
    }

    // Sets only the entity's own flag. The per-year flags and the new-item rule travel on no member
    // of this request, so nothing else under the entity becomes wanted: the scenes the reader marked
    // are the only ones this leaves monitored.
    private static async Task<bool?> MonitorOwningEntityAsync(
        WhisparrEntityKind owning,
        string foreignId,
        MonitoringTarget target,
        ILogger log,
        CancellationToken ct)
    {
        if (target.Reads is not IWhisparrStudioActing acting
            || ReadingEntity(owning, target) is not { } reading)
        {
            return null;
        }

        var held = await ContainedAsync(() => reading(foreignId, ct), target, log, ct)
            .ConfigureAwait(false);
        if (held is null || MonitoringProjector.EntityIdIn(held.Body) is not { } entityId)
        {
            return null;
        }

        var answered = await ContainedAsync(
            () => acting.SetStudioMonitoredAsync(
                entityId, monitored: true, ct),
            target,
            log,
            ct).ConfigureAwait(false);

        return answered is null
            ? null
            : MonitoringProjector.Accepted(answered) is MonitorRefusalKind.None;
    }

    // The one verb on this surface that can make an instance download. The command names the
    // instance's own scene identifier and the browser holds only the provider's, so the scene is
    // read first. The command is then read back by its id, so a success status alone is never the
    // evidence. One read and no loop: nothing here says a release was taken.
    internal static async Task<Results<Ok<MissingSceneActionResult>, BadRequest, ForbiddenCode>>
        SearchMissingSceneAsync(
            MissingSceneRoute addressed,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(addressed);
        var (kind, coveId, providerSceneId) = addressed;
        var route = new EntityRoute(kind, coveId);
        var (_, _, _, log) = whisparr;

        // Re-checked here because the route declaration enforces nothing on a minimal API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadEntity(route, out _) || !IsBoundedSceneId(providerSceneId))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(whisparr, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(NothingWasSent(MissingSceneActionRefusal.DidNotReachWhisparr));
        }

        // A generation registering neither role has no implementation to hand over, so there is
        // nothing to compose and nothing was sent.
        if (target.Reads is not IWhisparrSceneStatusReading reading
            || target.Reads is not IWhisparrSceneSearchGrabbing searching)
        {
            return TypedResults.Ok(
                NothingWasSent(MissingSceneActionRefusal.CapabilityAbsentOnThisGeneration));
        }

        var answered = await ContainedAsync(
            () => reading.ReadSceneByRemoteIdAsync(
                providerSceneId, ct),
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
            () => searching.SearchSceneAsync(sceneId, ct),
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
            () => target.Reads.ReadCommandAsync(commandId, ct),
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

    private static MissingSceneActionResult NothingWasSent(MissingSceneActionRefusal refusal)
        => new(MissingSceneState.StatusUnknown, refusal);

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

    // A control character would reach a JSON body and a header-shaped answer's parser, and neither
    // provider issues one, so the bound is on the shape rather than on either provider's spelling.
    private static bool IsBoundedSceneId(string providerSceneId)
        => !string.IsNullOrWhiteSpace(providerSceneId)
            && providerSceneId.Length <= ProviderSceneIdBound
            && !providerSceneId.Any(char.IsControl)
            && providerSceneId.Trim().Length == providerSceneId.Length;
}

// The scene a mark addresses: the entity whose catalogue listed it, and the provider's identifier
// for the scene itself.
internal sealed record OwnedSceneAddress(
    WhisparrEntityKind Owning, int CoveId, string ProviderSceneId);
