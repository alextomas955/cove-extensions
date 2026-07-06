using Renamer.Planner;

namespace Renamer.Api;

/// <summary>
/// One file's planned renamer from the whole-library scan, carrying every <see cref="RenamerPlanItem"/>
/// field FLATTENED alongside an explicit <see cref="Kind"/> tag. A single-kind <c>/preview</c> response
/// never needed a per-item kind (the caller supplies one kind for the whole request), but a whole-library
/// scan spans Video/Image/Audio in one payload, so the wire shape needs the tag to drive the Dry Run
/// table's "Type" column. Properties are flattened (not a nested <c>{ kind, item }</c> shape) to match
/// the frontend's <c>ScanItem extends PreviewItem</c> wire contract.
/// </summary>
public sealed record ScanItem(
    RenamerFileKind Kind,
    int EntityId,
    int FileId,
    string OldFullPath,
    string NewFullPath,
    RenamerStatus Status,
    string NewBasename,
    string TargetFolderPath,
    string? Reason,
    bool Suffixed,
    bool Sanitized,
    string? ResolvedDestinationRoot,
    string MatchedRule,
    string TargetVolume)
{
    /// <summary>Wraps a planned <paramref name="item"/> with its entity <paramref name="kind"/> for the wire response.</summary>
    /// <remarks>
    /// <paramref name="entityId"/> is the Cove entity id the whole plan is for (<see cref="RenamerPlan.EntityId"/>),
    /// NOT the per-file <see cref="RenamerPlanItem.FileId"/> — the UI builds the asset detail link from it, so every
    /// file of the same entity shares the one entity id.
    /// </remarks>
    public static ScanItem From(RenamerFileKind kind, int entityId, RenamerPlanItem item) => new(
        kind,
        entityId,
        item.FileId,
        item.OldFullPath,
        item.NewFullPath,
        item.Status,
        item.NewBasename,
        item.TargetFolderPath,
        item.Reason,
        item.Suffixed,
        item.Sanitized,
        item.ResolvedDestinationRoot,
        item.MatchedRule,
        item.TargetVolume);
}
