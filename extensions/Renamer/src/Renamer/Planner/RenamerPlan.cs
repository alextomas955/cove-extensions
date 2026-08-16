namespace Renamer.Planner;

/// <summary>
/// The shared classification vocabulary for a planned per-file renamer. Both halves speak this one
/// enum: the dry-run planner classifies what it INTENDS from the library it can read, and the
/// executor classifies what actually HAPPENED once disk and database were touched — so which half
/// produced a status is not something a member name reveals, and each member's own summary says.
/// <para>
/// Two members are produced by BOTH halves, which is the part worth stating because nothing about
/// them looks dual: <see cref="SkipMissingSource"/> comes from the executor's move-time source
/// pre-check AND the preview planner's read-only source-presence check, and
/// <see cref="SkipCollision"/> from the planner's suffix loop AND the executor's execution-time
/// re-check of the same collision against a now-staler snapshot.
/// </para>
/// </summary>
public enum RenamerStatus
{
    /// <summary>In-place basename change (same parent folder).</summary>
    Rename,

    /// <summary>Basename change AND a parent-folder move.</summary>
    Move,

    /// <summary>The rendered target equals the current path — nothing to do.</summary>
    NoOp,

    /// <summary>
    /// A taken target the suffix loop could not free. The executor must NOT attempt a move.
    /// </summary>
    SkipCollision,

    /// <summary>Gating (only-organized / require-fields) excluded this item.</summary>
    SkipGated,

    /// <summary>
    /// An exclude rule (tag / studio incl. parent / source-path) matched — the item is
    /// skipped-with-reason for EVERY one of its files (it is never rendered or moved). Kept DISTINCT
    /// from <see cref="SkipGated"/> (a gating skip) so the whole-batch preview and the run log can
    /// attribute an exclude correctly rather than conflating it with a gate. The matched exclude rule
    /// label travels in the item's <see cref="RenamerPlanItem.Reason"/>.
    /// </summary>
    SkipExcluded,

    /// <summary>Executor-only: the source file was locked/in-use at move time.</summary>
    SkipLocked,

    /// <summary>
    /// Executor- AND preview-produced: the source row exists in the DB but its file is absent on
    /// disk. Kept DISTINCT from <see cref="SkipLocked"/> (a file-lock skip) so run output, the log,
    /// and the preview attribute a genuinely-gone file correctly rather than reporting it as in-use.
    /// The executor emits it from a move-time source pre-check; the preview planner emits it from a
    /// read-only source-presence check.
    /// </summary>
    SkipMissingSource,

    /// <summary>
    /// Batch-only: the destination volume dropped below the free-space headroom in flight (a
    /// concurrent writer shrank it between the up-front admit and this item's copy), so the item was
    /// skipped rather than fill the disk. Kept distinct from <see cref="SkipLocked"/> (a file-lock
    /// skip) so log/monitor output attributes a disk-full skip correctly.
    /// </summary>
    SkipNoSpace,

    /// <summary>
    /// Executor-only: the destination was REFUSED by the canonical write-boundary guard
    /// (<c>Renamer.Execution.CanonicalPathGuard</c>) because its real on-disk target resolves
    /// outside every configured allowed root (a junction/symlink/8.3/UNC escape), or its rendered
    /// basename is not a single path segment. A SECURITY denial — kept distinct from
    /// <see cref="SkipCollision"/> (a name-taken skip) so run output and log monitoring can tell a
    /// policy block apart from a benign collision.
    /// </summary>
    SkipBlocked,

    /// <summary>Executor-only: the DB save failed after a disk move and was rolled back.</summary>
    Failed,

    /// <summary>
    /// Executor-only: the OS refused the move for want of permission — across volumes that covers the
    /// copy, the promote and the source delete alike. Kept DISTINCT from <see cref="SkipLocked"/> (a
    /// file-lock skip) because the two ask a maintainer for opposite responses: a lock clears by
    /// itself once whatever holds the file lets go, while a denial persists until someone changes an
    /// access rule, so log monitoring that conflates them reports a standing misconfiguration as
    /// transient contention and nobody ever acts on it.
    /// </summary>
    SkipPermissionDenied,

    /// <summary>
    /// Executor-only, cross-volume only: the destination read-back did not match the source by size
    /// or content hash, so the copy was rejected, the suspect destination deleted and the source left
    /// intact. Kept DISTINCT from <see cref="SkipLocked"/> (a file-lock skip) because this names a
    /// destination that returned different bytes than it was handed — the item was refused AFTER
    /// being written rather than never started, which is the signal to distrust the volume or the
    /// transport rather than to retry the item.
    /// </summary>
    SkipVerifyFailed,

    /// <summary>
    /// Executor-only: the move was cancelled in flight (a host shutdown), so the in-flight copy was
    /// removed and the source left untouched. Kept DISTINCT from <see cref="SkipLocked"/> (a file-lock
    /// skip) to honour the invariant that work interrupted by shutdown classifies as cancelled and
    /// never as a defect: reported as a lock skip, a clean stop tells a maintainer reading logs after
    /// a restart that those files were in use when in fact nothing held them.
    /// </summary>
    SkipCancelled,

    /// <summary>
    /// Planner-only: the item's destination measures from "the file's own library path" and the file
    /// lies under none of Cove's configured library paths — so there is no anchor to measure it from
    /// that the move leaves standing. Kept DISTINCT from <see cref="SkipCollision"/> (a name-taken
    /// skip) and from <see cref="SkipBlocked"/> (a security denial) because it is neither: the
    /// destination is not forbidden, it cannot be computed. The item keeps its current name and folder
    /// rather than being placed relative to its own parent — see
    /// <see cref="IRenamerDataPort.LibraryRoots"/> for why that arrangement is refused.
    /// <para>
    /// A destination whose root was CHOSEN from the library paths never reaches this status — it names
    /// its anchor outright. Whether that chosen root is still one of Cove's library paths is a
    /// different question, answered by <see cref="SkipRootMissing"/>.
    /// </para>
    /// </summary>
    SkipUnanchored,

    /// <summary>
    /// Planner-only: the destination's root was chosen from Cove's library paths and is no longer one
    /// of them — the user removed or re-pointed that path in Cove. Every file of the item is skipped
    /// with the rule named, and the run does not fail.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a fall-through to the default destination. A rule that cannot be honoured has
    /// no opinion about where its items go, and quietly handing them to the default would move files
    /// somewhere the user never chose BECAUSE their rule broke — a silent bulk relocation triggered by
    /// an unrelated edit in Cove's own settings. Kept DISTINCT from <see cref="SkipUnanchored"/>
    /// because the two ask for opposite actions: this one is fixed by re-picking a root in Renamer, that
    /// one by adding a folder to Cove's library.
    /// </remarks>
    SkipRootMissing,

    /// <summary>
    /// Planner-only: the rendered destination lies outside the area the configuration permits writing
    /// into — a folder template that is not relative, a template that traversed out of its own anchor,
    /// or an <c>AllowedRoots</c> list that does not cover it. Those are the three refusals
    /// <c>PathConfinement.Resolve</c> can produce, in the order it takes them. The item keeps its current
    /// name and folder. Kept DISTINCT from <see cref="SkipCollision"/> (a name-taken skip) because the
    /// two ask for different actions, and from <see cref="SkipBlocked"/>, which is the executor's
    /// disk-resolving guard refusing a real on-disk target at move time; this one is a pure string
    /// decision made before anything is touched, so the two can disagree and each is worth reading.
    /// </summary>
    SkipNotAllowed,

    /// <summary>
    /// Planner-only: the resolved absolute destination path is longer than
    /// <see cref="Options.RenamerOptions.FullPathMax"/>, so the item keeps its current name and folder.
    /// Kept DISTINCT from <see cref="SkipCollision"/> (a name-taken skip) because the two ask a
    /// maintainer for opposite responses: a collision clears by itself once the other file moves, while
    /// an over-long path stands until someone shortens the template or picks a shallower destination, so
    /// reading them as one reason means waiting for a state that never arrives.
    /// <para>
    /// WHAT THIS STATUS DOES NOT COVER, stated because the name reads wider than the check behind it:
    /// the budget is measured ONCE, against the name the template rendered, BEFORE the duplicate-suffix
    /// loop runs. That loop lengthens the name to free a taken slot and nothing re-measures it, so a
    /// suffixed path CAN exceed the budget and still be planned, previewed and written to disk. This
    /// status is evidence that one path was refused for length; it is never evidence that every planned
    /// path fits.
    /// </para>
    /// </summary>
    SkipTooLong,
}

/// <summary>
/// One file's planned renamer: its current full path, the intended new full path, the
/// classification the executor consumes, and the resolved pieces (new basename + absolute target
/// folder) the executor needs to perform the move. Immutable.
/// </summary>
/// <param name="FileId">The Cove <c>BaseFileEntity.Id</c> this item plans.</param>
/// <param name="OldFullPath">The file's current full path (<c>ParentFolderPath/Basename</c>, forward-slash).</param>
/// <param name="NewFullPath">The intended new full path (forward-slash), or the old path for a <see cref="RenamerStatus.NoOp"/>/skip.</param>
/// <param name="Status">The planner's classification.</param>
/// <param name="NewBasename">The resolved new basename (name + ext) the executor sets on the row.</param>
/// <param name="TargetFolderPath">The resolved absolute target folder path (forward-slash); equals the source folder for an in-place renamer.</param>
/// <param name="Reason">Human-readable reason for a skip/no-op (null for a plain renamer/move).</param>
/// <param name="Suffixed">UI badge signal: true iff the collision suffix loop ran (a number was appended to free the name). Defaults false; set only on the final Renamer/Move item.</param>
/// <param name="Sanitized">UI badge signal: true iff the engine cleaned the rendered name (illegal chars / spaces changed). Defaults false; set only on the final Renamer/Move item.</param>
/// <param name="ResolvedDestinationRoot">The Cove library path the destination was measured from — the root the rule (or the default) chose, or the one containing the file when it chose "the file's own library path". <c>null</c> when the item does not move and so anchored on nothing. Present only on a Renamer/Move item.</param>
/// <param name="MatchedRule">The resolver's matched-rule label (e.g. <c>"Studio:42(direct)"</c>, <c>"Tag:anime"</c>, <c>"Default"</c>) for preview/log. Defaults <c>""</c> on skip/no-op.</param>
/// <param name="TargetVolume">The destination volume (<see cref="Path.GetPathRoot(string)"/> of the resolved absolute target), set only on the final Renamer/Move item; consumed by the free-space sum and the cross-drive preview flag. Defaults <c>""</c>.</param>
/// <param name="DerivedTitle">
/// The filename-derived title the executor records on the entity in the same save as this rename, or
/// <c>null</c> when the item keeps a stored title, the fallback is off, or the item does not act. Set
/// only on a final Renamer/Move item, because a plan that changes nothing must write nothing.
/// </param>
/// <param name="OffLibraryDestination">
/// UI badge signal: true iff this item will act and is renamed where it already sits, in a folder under
/// none of Cove's configured library paths — so the file stays outside everything Cove scans. Not an
/// error and never a block: the rename happens exactly as previewed, and nothing about the new name or
/// folder changes. What it costs is worth stating before a bulk rename — a rescan never re-examines the
/// file, and if anything later moves it on disk Cove cannot rediscover it, at which point the next Clean
/// drops its row.
/// <para>
/// A rule's own destination never lands here, and this flag must not be read as promising otherwise:
/// every destination measures from a Cove library path, so a rule that still resolves writes inside the
/// library, and one whose root has gone is a <see cref="RenamerStatus.SkipRootMissing"/> or a
/// <see cref="RenamerStatus.SkipUnanchored"/> that never reaches this flag. Keyed on the resolved
/// DESTINATION folder rather than on the route, which leaves the in-place rename above as the one
/// reachable case. False when the host declares no library path at all, where "outside the scanned set"
/// describes every file equally and so warns about nothing.
/// </para>
/// </param>
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
    string? DerivedTitle = null,
    bool OffLibraryDestination = false);

/// <summary>
/// The dry-run output of <c>RenamerPlanner.PlanAsync</c>: one <see cref="RenamerPlanItem"/> per
/// physical file of the entity (every file, never just the first), plus the entity id/kind it
/// planned. Carries NO disk/DB mutation — it is a preview only.
/// </summary>
/// <param name="EntityId">The planned entity's id.</param>
/// <param name="Kind">The planned entity's kind.</param>
/// <param name="Items">One item per file, in file order.</param>
public sealed record RenamerPlan(
    int EntityId,
    RenamerFileKind Kind,
    IReadOnlyList<RenamerPlanItem> Items);
