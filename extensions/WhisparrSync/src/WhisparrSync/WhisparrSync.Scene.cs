using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using WhisparrSync.Connection;
using WhisparrSync.Contracts;
using WhisparrSync.Library;
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
    /// Nothing is cached. The read happens on every open, so what the tab states is what the
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

        return TypedResults.Ok(SceneDetailProjector.Project(answered));
    }

    /// <summary>An answer claiming nothing about the instance, and the reason it claims nothing.</summary>
    private static SceneDetailView NothingWasSent(SceneRefusalKind refusal)
        => new(refusal, false, null, null, null, null, null, false);
}
