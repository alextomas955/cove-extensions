using System.Text.Json.Serialization;
using Cove.Extensions.Shared;

namespace Renamer.Planner;

// The classification of one planned or executed file rename. Some members come from the planner, some
// from the executor, and SkipMissingSource from both. Each skip is its own member because each clears
// differently and asks the user for something different.
[JsonConverter(typeof(CamelCaseStringEnumConverter))]
public enum RenamerStatus
{
    // In-place basename change, same parent folder.
    Rename,

    // Basename change and a parent-folder move.
    Move,

    // The rendered target equals the current path.
    NoOp,

    // A taken target the suffix loop could not free. The executor must not attempt a move.
    SkipCollision,

    // Gating, the kind switch, only-organized or require-fields, excluded this item.
    SkipGated,

    // An exclude rule matched on tag, studio including parents, or source path. Every file of the item
    // is skipped; the matched rule travels in the Reason.
    SkipExcluded,

    // Planner-only: a source-path regex rule, routing or exclude, timed out on the item's folder. The
    // rule's pattern travels in the reason.
    SkipRuleTimedOut,

    // Executor-only: the source file was locked or in use at move time.
    SkipLocked,

    // Executor and preview: the source row exists in the database but its file is absent on disk.
    SkipMissingSource,

    // Batch-only: the destination volume dropped below the free-space headroom in flight, after the
    // up-front check admitted the batch.
    SkipNoSpace,

    // Executor-only: the database save failed after a disk move, and the move was rolled back.
    Failed,

    // Planner-only: the destination measures from the Cove library path holding the file, and the file
    // lies under none of them. Fixed by adding the folder to Cove's library.
    SkipUnanchored,

    // Planner-only: the destination's chosen root is no longer one of Cove's library paths. The item is
    // skipped rather than sent to the default destination, which would relocate files in bulk because
    // of an unrelated edit in Cove's settings. Fixed by re-picking the root in Renamer.
    SkipRootMissing,

    // Planner-only: the rendered destination lies outside the area the configuration permits writing
    // into, such as a folder template that is not relative or that traverses out of its root.
    SkipNotAllowed,

    // The resolved absolute path is longer than RenamerOptions.FullPathMax. It is measured against the
    // rendered name and again after the suffix loop lengthens it; the executor repeats the second
    // measurement against its fresher snapshot, before anything is written.
    SkipTooLong,

    // Executor-only: the OS denied permission for the copy, the promote or the source delete. Unlike a
    // lock, it persists until someone changes an access rule.
    SkipPermissionDenied,

    // Executor-only, cross-volume only: the destination read-back did not match the source by size or
    // hash, so the copy was deleted and the source left intact.
    SkipVerifyFailed,

    // Executor-only: a host shutdown cancelled the move in flight, the in-flight copy was removed and
    // the source left untouched.
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
