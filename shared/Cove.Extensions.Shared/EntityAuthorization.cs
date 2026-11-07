using Cove.Core.Auth;

namespace Cove.Extensions.Shared;

/// <summary>Decides which entities a caller may act on, and copies a caller for a detached body.</summary>
/// <remarks>
/// A request path and the job body it enqueues both have to reach the same decision, and the body
/// cannot re-resolve the principal once the request has ended.
/// </remarks>
public static class EntityAuthorization
{
    /// <summary>
    /// The subset of <paramref name="ids"/> the principal holds <paramref name="permission"/> over
    /// for <paramref name="entityKind"/>, in the supplied order.
    /// </summary>
    /// <remarks>
    /// A principal holding <see cref="Permissions.All"/> is returned the input with no authorization
    /// call. The membership test is literal: a wildcard-expanding helper answers true for a caller
    /// whose grant is narrower than the whole library, which would drop the per-entity decision.
    /// <para>
    /// <paramref name="entityKind"/> is a Cove entity-kind string, as declared by
    /// <see cref="Cove.Core.Entities.EntityKinds"/>.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyList<int>> AllowedOnlyAsync(
        IAuthorizationService authz, CovePrincipal? caller, string entityKind, string permission,
        IReadOnlyList<int> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authz);
        ArgumentNullException.ThrowIfNull(ids);

        if (ids.Count == 0 || caller?.Permissions.Contains(Permissions.All) == true)
        {
            return ids;
        }

        var entities = new List<EntityRef>(ids.Count);
        foreach (int id in ids)
        {
            entities.Add(EntityRef.Of(entityKind, id));
        }

        var results = await authz.AuthorizeManyAsync(caller, permission, entities, ct);

        var allowed = new List<int>(ids.Count);
        for (int i = 0; i < ids.Count; i++)
        {
            if (results[i].Allowed)
            {
                allowed.Add(ids[i]);
            }
        }

        return allowed;
    }

    /// <summary>Copies a principal so a detached job body can authorize against it after the request ends.</summary>
    public static CovePrincipal? Snapshot(CovePrincipal? principal)
    {
        if (principal is null)
        {
            return null;
        }

        return new CovePrincipal
        {
            UserId = principal.UserId,
            Username = principal.Username,
            Kind = principal.Kind,
            Roles = principal.Roles.ToHashSet(StringComparer.OrdinalIgnoreCase),
            Permissions = principal.Permissions.ToHashSet(StringComparer.OrdinalIgnoreCase),
            ReadRestrictedEntityKinds = principal.ReadRestrictedEntityKinds.ToHashSet(StringComparer.OrdinalIgnoreCase),
            ReadGrantedEntityKinds = principal.ReadGrantedEntityKinds.ToHashSet(StringComparer.OrdinalIgnoreCase),
            ClaimsPrincipal = principal.ClaimsPrincipal,
            TokenId = principal.TokenId,
            Ip = principal.Ip,
            UserAgent = principal.UserAgent,
        };
    }
}
