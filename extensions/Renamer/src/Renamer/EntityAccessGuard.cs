using Cove.Core.Auth;
using Cove.Core.Entities;

namespace Renamer;

/// <summary>Narrows a page of entity ids to the ones a principal may act on.</summary>
internal delegate Task<IReadOnlyList<int>> AllowedIds(
    RenamerFileKind kind, IReadOnlyList<int> ids, CancellationToken ct);

// The request path and the detached job bodies both authorize entities, so the decision and the
// principal copy live in one place rather than once per caller.
internal static class EntityAccessGuard
{
    internal static string EntityKindOf(RenamerFileKind kind) => kind switch
    {
        RenamerFileKind.Image => EntityKinds.Image,
        RenamerFileKind.Audio => EntityKinds.Audio,
        RenamerFileKind.Text => EntityKinds.Text,
        RenamerFileKind.Video => EntityKinds.Video,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a renamable kind"),
    };

    /// <summary>
    /// The subset of <paramref name="ids"/> the principal holds <paramref name="permission"/> over,
    /// in the supplied order.
    /// </summary>
    /// <remarks>
    /// A principal holding <see cref="Permissions.All"/> is returned the input with no authorization
    /// call. The membership test is literal: a wildcard-expanding helper answers true for a caller
    /// whose grant is narrower than the whole library, which would drop the per-entity decision.
    /// </remarks>
    internal static async Task<IReadOnlyList<int>> AllowedOnlyAsync(
        IAuthorizationService authz, CovePrincipal? caller, RenamerFileKind kind, string permission,
        IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0 || caller?.Permissions.Contains(Permissions.All) == true)
        {
            return ids;
        }

        string entityKind = EntityKindOf(kind);
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
    internal static CovePrincipal? Snapshot(CovePrincipal? principal)
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
