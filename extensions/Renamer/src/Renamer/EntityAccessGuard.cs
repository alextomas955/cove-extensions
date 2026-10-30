using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Extensions.Shared;

namespace Renamer;

/// <summary>Narrows a page of entity ids to the ones a principal may act on.</summary>
internal delegate Task<IReadOnlyList<int>> AllowedIds(
    RenamerFileKind kind, IReadOnlyList<int> ids, CancellationToken ct);

// The request path and the detached job bodies both authorize entities, so the kind mapping this
// extension owns sits beside the shared decision rather than once per caller.
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

    internal static Task<IReadOnlyList<int>> AllowedOnlyAsync(
        IAuthorizationService authz, CovePrincipal? caller, RenamerFileKind kind, string permission,
        IReadOnlyList<int> ids, CancellationToken ct)
        => EntityAuthorization.AllowedOnlyAsync(
            authz, caller, EntityKindOf(kind), permission, ids, ct);

    internal static CovePrincipal? Snapshot(CovePrincipal? principal)
        => EntityAuthorization.Snapshot(principal);
}
