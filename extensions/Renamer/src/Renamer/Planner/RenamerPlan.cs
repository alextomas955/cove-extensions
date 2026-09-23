using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace Renamer.Planner;

// The shared classification vocabulary for a planned per-file rename. Some members are produced by the
// planner, some by the executor, and SkipMissingSource by both halves; they live in one enum so the two
// speak the same language.
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum RenamerStatus
{
    // In-place basename change, same parent folder.
    Renamer,

    // Basename change and a parent-folder move.
    Move,

    // The rendered target equals the current path.
    NoOp,

    // A taken target the suffix loop could not free. The executor must not attempt a move.
    SkipCollision,

    // Gating, only-organized or require-fields, excluded this item.
    SkipGated,

    // An exclude rule matched on tag, studio including parents, or source-path. Every file of the item
    // is skipped with a reason and never rendered or moved. Kept apart from SkipGated so the preview and
    // the run log attribute an exclude correctly; the matched rule label travels in the item's Reason.
    SkipExcluded,

    // Planner-only: a source-path regex rule, routing or exclude, timed out on the item's folder. The
    // rule's pattern travels in the reason.
    SkipRuleTimedOut,

    // Executor-only: the source file was locked or in use at move time.
    SkipLocked,

    // Executor and preview: the source row exists in the DB but its file is absent on disk. Kept apart
    // from SkipLocked so output attributes a genuinely gone file correctly.
    SkipMissingSource,

    // Batch-only: the destination volume dropped below the free-space headroom in flight, because a
    // concurrent writer shrank it between the up-front admit and this item's copy, so the item was
    // skipped rather than fill the disk. Kept apart from SkipLocked so a disk-full skip is attributed
    // correctly.
    SkipNoSpace,

    // Executor-only: the DB save failed after a disk move and was rolled back.
    Failed,

    // Planner-only: the destination measures from the Cove library path holding the file, and the file
    // lies under none of them, so no anchor is left standing by the move it names. The item keeps its
    // current name and folder. Kept apart from SkipNotAllowed: the destination is not refused, it cannot
    // be computed.
    SkipUnanchored,

    // Planner-only: the destination's chosen root is no longer one of Cove's library paths. Every file
    // of the item is skipped with the rule named, and the run does not fail. Deliberately not a
    // fall-through to the default destination, which would relocate files in bulk because an unrelated
    // edit in Cove's own settings broke a rule. Fixed by re-picking a root in Renamer, where
    // SkipUnanchored is fixed by adding a folder to Cove's library.
    SkipRootMissing,

    // Planner-only: the rendered destination lies outside the area the configuration permits writing
    // into - a folder template that is not relative, or one that traversed out of its own root. A pure
    // string decision, taken before anything is touched.
    SkipNotAllowed,

    // The resolved absolute destination path is longer than RenamerOptions.FullPathMax, so the item
    // keeps its current name and folder. Kept apart from SkipCollision because the two clear
    // differently: a collision clears by itself once the other file moves, while an over-long path
    // stands until someone shortens the template or picks a shallower destination.
    //
    // The budget is measured against the rendered name, and again against the name a duplicate-suffix
    // loop settles on, because that loop lengthens the name. The executor repeats the second measurement
    // because its own loop re-suffixes against a fresher snapshot than the plan saw; that emission
    // arrives after the confirm gate and before anything is written.
    SkipTooLong,

    // Executor-only: the OS refused the move for want of permission, covering the copy, the promote and
    // the source delete alike. Kept apart from SkipLocked because the two ask a maintainer for opposite
    // responses: a lock clears by itself, while a denial persists until someone changes an access rule,
    // so conflating them reports a standing misconfiguration as transient contention.
    SkipPermissionDenied,

    // Executor-only, cross-volume only: the destination read-back did not match the source by size or
    // content hash, so the copy was rejected, the suspect destination deleted and the source left
    // intact. Kept apart from SkipLocked because this names a destination that returned different bytes
    // than it was handed, which is the signal to distrust the volume or the transport and not to retry
    // the item.
    SkipVerifyFailed,

    // Executor-only: the move was cancelled in flight by a host shutdown, so the in-flight copy was
    // removed and the source left untouched. Kept apart from SkipLocked so work interrupted by shutdown
    // classifies as cancelled and never as a defect.
    SkipCancelled,
}

// One file's planned rename: its current full path, the intended new full path, the classification the
// executor consumes, and the resolved new basename and absolute target folder the executor needs to
// perform the move. Paths are forward-slash. NewFullPath is the old path for a no-op or a skip, and
// Reason is null for a plain rename or move.
//
// Suffixed and Sanitized are UI badge signals, ResolvedDestinationRoot, MatchedRule and TargetVolume
// carry the routing facts, and DerivedTitle is the filename-derived title the executor records on the
// entity in the same save as the rename. All of them are set only on a final acting item, because a
// plan that changes nothing must write nothing. DerivedTitle is also null when the item keeps a stored
// title or the fallback is off; see MetadataProjector.DerivedTitle.
public sealed record RenamerPlanItem(
    int FileId,
    string OldFullPath,
    string NewFullPath,
    RenamerStatus Status,
    string NewBasename,
    string TargetFolderPath,
    string? Reason = null,
    bool Suffixed = false,
    bool Sanitized = false,
    string? ResolvedDestinationRoot = null,
    string MatchedRule = "",
    string TargetVolume = "",
    string? DerivedTitle = null);

// The dry-run output of the planner: one item per physical file of the entity, in file order, plus the
// entity id and kind it planned. It carries no disk or DB mutation.
public sealed record RenamerPlan(
    int EntityId,
    RenamerFileKind Kind,
    IReadOnlyList<RenamerPlanItem> Items);
