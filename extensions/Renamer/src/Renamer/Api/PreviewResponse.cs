using Renamer.Planner;

namespace Renamer.Api;

/// <summary>
/// The <c>/preview</c> response body: the per-item plan PLUS the whole-batch blast-radius
/// summary. Replaces the former bare <c>RenamerPlanItem[]</c> so the dry-run can carry batch-level
/// aggregates (count, same/cross split, per-volume bytes, the scaled confirm level) without losing
/// the per-item array contract the UI matches on (<c>status === "Renamer"</c>). Both halves ride the
/// same camelCase + string-enum serializer, so <see cref="Items"/> keeps its exact wire shape and
/// <see cref="PreviewSummary.ConfirmLevel"/> serializes as "Light"/"Standard"/"Heavy".
/// </summary>
/// <param name="Items">One plan item per physical file of the selection, in plan order.</param>
/// <param name="Summary">The whole-batch blast radius computed over the acting items.</param>
public sealed record PreviewResponse(
    IReadOnlyList<RenamerPlanItem> Items,
    PreviewSummary Summary);
