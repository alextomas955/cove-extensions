using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Missing;
using WhisparrSync.Monitoring;
using WhisparrSync.Whisparr;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    private void MapMissingTrackEndpoints(IEndpointRouteBuilder endpoints)
    {
        // The offer is its own route rather than a member of the catalogue page: the refused paths
        // that build that page resolve no instance, and this has to answer on every one of them.
        endpoints.MapGet(MissingEntryOfferRoute,
            ([AsParameters] EntityRoute route, ICurrentPrincipalAccessor principal,
             WhisparrAccess whisparr, CancellationToken ct)
                => ReadMissingEntryOfferAsync(route, principal, whisparr, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ReadPermissions);

        endpoints.MapPost(MissingTrackRoute,
            ([AsParameters] EntityRoute route, MissingEntryRequest? request,
             ICurrentPrincipalAccessor principal, WhisparrAccess whisparr, MissingEntryAccess entry,
             CancellationToken ct)
                => AddEntityAsync(route, request, principal, whisparr, entry, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // Answered from the stored connection and the roles the resolved instance declares. Nothing
    // outbound is sent, so the choice opens at the same speed whatever the instance is doing.
    internal static async Task<Results<Ok<MissingEntryOffer>, BadRequest, ForbiddenCode>>
        ReadMissingEntryOfferAsync(
            EntityRoute route,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            CancellationToken ct)
    {
        // Re-checked here because the route declaration enforces nothing on a minimal API.
        if (!HasReadPermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadEntity(route, out var entityKind))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(whisparr, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(MissingEntryOffer.NotConfigured);
        }

        return TypedResults.Ok(
            new MissingEntryOffer(
                EntryModesOn(entityKind, target),
                GenerationCapabilities.AScopeChangeIsRetroactiveOn(target.Binding.Generation)));
    }

    // Read off the roles the resolved instance declares, never off a generation name. The narrower
    // scope is a studio fact: the field it is expressed through exists on the studio resource alone,
    // so every other kind is offered the one monitor mode.
    private static List<MissingEntryMode> EntryModesOn(
        WhisparrEntityKind kind, MonitoringTarget target)
    {
        var modes = new List<MissingEntryMode>();

        if (target.Reads is IWhisparrEntityTrackingActing)
        {
            modes.Add(MissingEntryMode.TrackOnly);
        }

        if (HeldActingFor(kind, target) is null)
        {
            return modes;
        }

        if (ExpressesAScope(kind))
        {
            modes.Add(MissingEntryMode.MonitorNewReleases);
        }

        modes.Add(MissingEntryMode.MonitorAllScenes);
        return modes;
    }

    // What a reader is missing is composed from the instance's own list, and an instance lists
    // nothing for an entity it has never been told about. The mode decides what the entity arrives
    // wanting.
    internal static async Task<Results<Ok<MissingTrackResult>, BadRequest, ForbiddenCode>>
        AddEntityAsync(
            EntityRoute route,
            MissingEntryRequest? request,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            MissingEntryAccess entry,
            CancellationToken ct)
    {
        var (_, coveId) = route;

        var (_, _, _, log) = whisparr;

        // Re-checked here because the route declaration enforces nothing on a minimal API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(entry);

        if (!TryReadEntity(route, out var entityKind))
        {
            return TypedResults.BadRequest();
        }

        if (await ResolveTargetAsync(whisparr, ct).ConfigureAwait(false)
            is not { } target)
        {
            return TypedResults.Ok(
                new MissingTrackResult(MissingTrackOutcome.NoInstanceConnected));
        }

        var mode = request?.Mode ?? MissingEntryMode.TrackOnly;

        return TypedResults.Ok(
            mode is MissingEntryMode.TrackOnly
                ? await TrackEntityAsync(entityKind, coveId, target, entry, ct)
                    .ConfigureAwait(false)
                : await MonitorEntityForItsCatalogueAsync(
                    entityKind, coveId, ScopeOf(mode), target, entry, log, ct)
                    .ConfigureAwait(false));
    }

    // The same operation the entity monitor button is carried out by, so the two cannot come to
    // behave differently for one gesture.
    private static async Task<MissingTrackResult> MonitorEntityForItsCatalogueAsync(
        WhisparrEntityKind kind,
        int coveId,
        MonitorScope scope,
        MonitoringTarget target,
        MissingEntryAccess entry,
        ILogger log,
        CancellationToken ct)
    {
        var (identities, cache, work) = entry;
        var (_, scopes) = work;

        var monitoring = await MonitorResolvedAsync(
            new MonitoredEntity(kind, coveId),
            target,
            identities,
            log,
            ActingFor(kind, target, scope),
            EntityRootThrough(scopes, target, FilesOfEntity(kind, coveId)),
            ct).ConfigureAwait(false);

        if (monitoring.Refusal is not MonitorRefusalKind.None)
        {
            return new MissingTrackResult(OutcomeOf(monitoring.Refusal));
        }

        cache.Forget();
        return new MissingTrackResult(MissingTrackOutcome.Added);
    }

    private static MonitorScope ScopeOf(MissingEntryMode mode)
        => mode switch
        {
            MissingEntryMode.MonitorNewReleases => MonitorScope.FutureScenes,
            MissingEntryMode.MonitorAllScenes => MonitorScope.AllScenes,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode), mode, "This mode names no monitor scope."),
        };

    // The monitoring vocabulary read into this surface's own. Several identities collapse into the
    // one kind that names no usable identifier, which is what the catalogue add already answers for
    // an identity it could not resolve.
    private static MissingTrackOutcome OutcomeOf(MonitorRefusalKind refusal)
        => refusal switch
        {
            MonitorRefusalKind.None => MissingTrackOutcome.Added,
            MonitorRefusalKind.NotConfigured => MissingTrackOutcome.NoInstanceConnected,
            MonitorRefusalKind.CapabilityAbsentOnThisGeneration
                => MissingTrackOutcome.GenerationCannotTrackThisKind,
            MonitorRefusalKind.NoIdentityInThisNamespace
                or MonitorRefusalKind.SeveralIdentitiesInThisNamespace
                => MissingTrackOutcome.NoIdentifier,
            MonitorRefusalKind.NoQualityProfile or MonitorRefusalKind.NoRootFolder
                => MissingTrackOutcome.NoAddDefaults,
            MonitorRefusalKind.NoAgreedRootForThisEntity
                => MissingTrackOutcome.NoAgreedRootForThisEntity,
            MonitorRefusalKind.InstanceRefused => MissingTrackOutcome.Refused,
            MonitorRefusalKind.AnswerTooLargeToRead
                or MonitorRefusalKind.InstanceHoldsNoSuchEntity
                or MonitorRefusalKind.InstanceDidNotReportTheChange
                => MissingTrackOutcome.NotStarted,
            _ => throw new ArgumentOutOfRangeException(
                nameof(refusal), refusal, "This refusal has no outcome written down for it."),
        };

    // Adds the entity so the instance tracks its catalogue, wanting none of it.
    private static async Task<MissingTrackResult>
        TrackEntityAsync(
            WhisparrEntityKind entityKind,
            int coveId,
            MonitoringTarget target,
            MissingEntryAccess entry,
            CancellationToken ct)
    {
        var (identities, cache, _) = entry;

        if (target.Reads is not IWhisparrEntityTrackingActing tracking)
        {
            return new MissingTrackResult(MissingTrackOutcome.GenerationCannotTrackThisKind);
        }

        var identity = await identities
            .ResolveAsync(entityKind, coveId, target.Binding.Generation, ct)
            .ConfigureAwait(false);
        if (identity.ForeignId is not { Length: > 0 } foreignId)
        {
            return new MissingTrackResult(MissingTrackOutcome.NoIdentifier);
        }

        // The defaults the instance itself declares, read here rather than assumed: an add naming a
        // profile or a root the instance does not hold is refused by it.
        var profiles = await ReadOrNullAsync(
            () => target.Reads.ReadQualityProfilesAsync(ct))
            .ConfigureAwait(false);
        var roots = profiles is null
            ? null
            : await ReadOrNullAsync(
                () => target.Reads.ReadRootFoldersAsync(ct))
                .ConfigureAwait(false);
        if (profiles is null || roots is null)
        {
            return new MissingTrackResult(MissingTrackOutcome.NotStarted);
        }

        if (AddDefaultsProjector.From(profiles.Body, roots.Body).Defaults is not { } defaults)
        {
            return new MissingTrackResult(MissingTrackOutcome.NoAddDefaults);
        }

        WhisparrResponse answered;
        try
        {
            answered = await tracking
                .TrackEntityAsync(
                    entityKind,
                    foreignId,
                    defaults,
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            return new MissingTrackResult(MissingTrackOutcome.NotStarted);
        }

        if (answered.StatusCode is < 200 or > 299)
        {
            return new MissingTrackResult(MissingTrackOutcome.Refused);
        }

        // What was held predates the entity, so the next read reaches the instance rather than
        // answering that it still holds nothing.
        cache.Forget();
        return new MissingTrackResult(MissingTrackOutcome.Added);
    }

    // A read that did not arrive answers null, so the caller reports it as nothing sent rather than
    // failing the request.
    private static async Task<WhisparrResponse?> ReadOrNullAsync(
        Func<Task<WhisparrResponse>> read)
    {
        try
        {
            return await read().ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException)
        {
            return null;
        }
    }
}
