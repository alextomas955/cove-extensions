using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
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
        // Configure tier: it creates an entity in the reader's Whisparr, which a caller who cannot
        // configure the extension may not do.
        endpoints.MapPost(MissingTrackRoute,
            ([AsParameters] EntityRoute route, ICurrentPrincipalAccessor principal, WhisparrAccess whisparr, IEntityIdentityPort identities,
             InstanceCatalogueCache cache, CancellationToken ct)
                => TrackEntityAsync(
                    route, principal, whisparr, identities, cache, ct))
            .WithTags(WireTag)
            .RequireCovePermission(PermissionMode.Any, ConfigurePermissions);
    }

    // Adds the entity so the instance tracks its catalogue, wanting none of it. What a reader is
    // missing is composed from the instance's own list, and an instance lists nothing for an entity
    // it has never been told about.
    internal static async Task<Results<Ok<MissingTrackResult>, BadRequest, ForbiddenCode>>
        TrackEntityAsync(
            EntityRoute route,
            ICurrentPrincipalAccessor principal,
            WhisparrAccess whisparr,
            IEntityIdentityPort identities,
            InstanceCatalogueCache cache,
            CancellationToken ct)
    {
        var (_, coveId) = route;

        // Re-checked here because the route declaration enforces nothing on a minimal API.
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(cache);

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

        if (target.Reads is not IWhisparrEntityTrackingActing tracking)
        {
            return TypedResults.Ok(
                new MissingTrackResult(MissingTrackOutcome.GenerationCannotTrackThisKind));
        }

        var identity = await identities
            .ResolveAsync(entityKind, coveId, target.Binding.Generation, ct)
            .ConfigureAwait(false);
        if (identity.ForeignId is not { Length: > 0 } foreignId)
        {
            return TypedResults.Ok(new MissingTrackResult(MissingTrackOutcome.NoIdentifier));
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
            return TypedResults.Ok(new MissingTrackResult(MissingTrackOutcome.NotStarted));
        }

        if (AddDefaultsProjector.From(profiles.Body, roots.Body).Defaults is not { } defaults)
        {
            return TypedResults.Ok(new MissingTrackResult(MissingTrackOutcome.NoAddDefaults));
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
            return TypedResults.Ok(new MissingTrackResult(MissingTrackOutcome.NotStarted));
        }

        if (answered.StatusCode is < 200 or > 299)
        {
            return TypedResults.Ok(new MissingTrackResult(MissingTrackOutcome.Refused));
        }

        // What was held predates the entity, so the next read reaches the instance rather than
        // answering that it still holds nothing.
        cache.Forget();
        return TypedResults.Ok(new MissingTrackResult(MissingTrackOutcome.Added));
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
