using Cove.Core.Auth;
using Cove.Extensions.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using WhisparrSync.Contracts;

namespace WhisparrSync;

public sealed partial class WhisparrSync
{
    /// <summary>Marks one catalogue scene as wanted, without acquiring it.</summary>
    internal static Task<Results<Ok<MissingSceneActionResult>, BadRequest, ForbiddenCode>>
        MonitorMissingSceneAsync(
            string kind, int coveId, string providerSceneId, ICurrentPrincipalAccessor principal)
        => Task.FromResult(MissingSceneAction(kind, coveId, providerSceneId, principal));

    /// <summary>Asks the connected instance to look for one catalogue scene.</summary>
    /// <remarks>The one verb on this surface that can make an instance download.</remarks>
    internal static Task<Results<Ok<MissingSceneActionResult>, BadRequest, ForbiddenCode>>
        SearchMissingSceneAsync(
            string kind, int coveId, string providerSceneId, ICurrentPrincipalAccessor principal)
        => Task.FromResult(MissingSceneAction(kind, coveId, providerSceneId, principal));

    private static Results<Ok<MissingSceneActionResult>, BadRequest, ForbiddenCode> MissingSceneAction(
        string kind, int coveId, string providerSceneId, ICurrentPrincipalAccessor principal)
    {
        if (!HasConfigurePermission(principal))
        {
            return new ForbiddenCode();
        }

        if (!TryReadEntity(kind, coveId, out _) || string.IsNullOrWhiteSpace(providerSceneId))
        {
            return TypedResults.BadRequest();
        }

        return TypedResults.Ok(new MissingSceneActionResult(
            MissingSceneState.StatusUnknown, MissingSceneActionRefusal.DidNotReachWhisparr));
    }
}
