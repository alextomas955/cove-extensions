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
