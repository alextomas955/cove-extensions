namespace Renamer.Execution;

/// <summary>
/// How a primary move attempt was classified. This is the single classification BOTH executor tiers
/// return — the same-volume <see cref="DiskMover"/> and the cross-volume <see cref="CrossVolumeMover"/>.
/// </summary>
/// <remarks>
/// Which members a given tier can actually produce is a property of that tier's own documented
/// contract, not of this type: an atomic same-volume rename has no copy to read back and no
/// cancellation point, so <see cref="DiskMover"/> never returns <see cref="VerifyFailed"/> or
/// <see cref="Cancelled"/> — its own <c>Move</c> summary says so.
/// <para>
/// One type rather than one per tier, so that a switch over an outcome is total over every outcome the
/// executor can meet. With an enum per mover, a member added to one is guarded only by the switch that
/// happens to name that mover's type, and the compiler's totality check is bought per switch rather
/// than outright.
/// </para>
/// <para>
/// The ordinals are pinned so that merging the two tiers' enums into this one could not silently
/// renumber either of them; nothing persists or transmits this value, but a renumber would have been
/// invisible at the call sites the merge moved.
/// </para>
/// </remarks>
public enum MoveOutcome
{
    /// <summary>The primary file moved old→new: on the same volume an atomic rename; across volumes a
    /// copy that was verified, atomically promoted, and whose source was then deleted.</summary>
    Moved = 0,

    /// <summary>The source was locked/in-use OR the destination already existed. The move never
    /// completed, so the source stays at its old path and nothing was overwritten.</summary>
    LockedOrExists = 1,

    /// <summary>The OS denied permission for the move — across volumes, for the copy, the promote or
    /// the source delete.</summary>
    PermissionDenied = 2,

    /// <summary>The destination read-back did not match the source by size or content hash — the copy
    /// was rejected, the suspect destination deleted, and the source left intact.</summary>
    VerifyFailed = 3,

    /// <summary>The caller cancelled the <see cref="CancellationToken"/> mid-move. The in-flight copy
    /// that call created is removed and the source is left untouched — a cancel never loses or
    /// duplicates a file and never throws out (classify-not-throw).</summary>
    Cancelled = 4,
}
