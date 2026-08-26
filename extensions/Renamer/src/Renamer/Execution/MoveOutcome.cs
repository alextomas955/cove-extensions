using Renamer.Planner;

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

    /// <summary>The source was locked/in-use at move time — the move never started, so the source
    /// stays at its old path and no destination was created.</summary>
    Locked,

    /// <summary>The OS denied permission for the move — across volumes, for the copy, the promote or
    /// the source delete.</summary>
    PermissionDenied,

    /// <summary>The destination read-back did not match the source by size or content hash — the copy
    /// was rejected, the suspect destination deleted, and the source left intact.</summary>
    VerifyFailed,

    /// <summary>The caller cancelled the <see cref="CancellationToken"/> mid-move. The in-flight copy
    /// that call created is removed and the source is left untouched — a cancel never loses or
    /// duplicates a file and never throws out (classify-not-throw).</summary>
    Cancelled,

    /// <summary>The destination path was occupied when the move needed it — never overwritten. Both
    /// files survive: whatever holds the destination name, and the source at its old path.</summary>
    /// <remarks>
    /// Appended last so that splitting this cause out of the old merged member could not renumber any
    /// member above it — the same reason the ordinals are pinned at all.
    /// <para>
    /// Where the operating system raises one <see cref="IOException"/> for both an occupied destination
    /// and a locked source, the tier that catches it tells them apart by TESTING the destination, never
    /// by reading the exception's message: that message is prose, and a decision keyed on it changes
    /// meaning the moment someone rewords it.
    /// </para>
    /// </remarks>
    TargetExists,
}

/// <summary>
/// Translates a mover's classification of a move that did NOT happen into the status that move reports.
/// </summary>
/// <remarks>
/// Pure by construction: no filesystem, no database, no host runtime, and no matching on the
/// human-readable reason that travels beside an outcome — that reason is prose written for a person, and
/// a decision keyed on it changes meaning the moment someone rewords it.
/// <para>
/// Every member is named and there is NO discard arm, so the compiler refuses a member added to
/// <see cref="MoveOutcome"/> that nobody gave a status of its own. A discard arm would compile the same
/// code while quietly answering for members no one had considered, which is the collapse this class
/// exists to end: one status used to stand for a lock, a denial, a failed verify and a clean shutdown
/// alike, and an operator could not act on the difference because the difference never left here.
/// </para>
/// </remarks>
public static class MoveOutcomeClassifier
{
    /// <summary>
    /// The status a move attempt classified <paramref name="outcome"/> is reported under.
    /// </summary>
    /// <param name="outcome">The mover's own classification of an attempt that did not move the file.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="outcome"/> is <see cref="MoveOutcome.Moved"/>. A move that happened takes the
    /// planner's own status for the item, so there is nothing here to translate.
    /// </exception>
    /// <remarks>
    /// The throwing <see cref="MoveOutcome.Moved"/> arm is the OPPOSITE choice from
    /// <c>UndoReplayer.StopFor</c>, which maps its unreachable <see cref="MoveOutcome.Moved"/> to a
    /// retryable reason instead, and the asymmetry is deliberate rather than an oversight in either
    /// place. There, a wrong answer retires a journal row and takes with it the only route a user has
    /// back to their file, so the safe direction is to answer harmlessly. Here nothing is retired and
    /// no recovery is at stake, so a caller that reached this arm has broken the contract above and is
    /// better told than accommodated.
    /// </remarks>
    public static RenamerStatus StatusFor(MoveOutcome outcome) => outcome switch
    {
        MoveOutcome.Locked => RenamerStatus.SkipLocked,
        MoveOutcome.TargetExists => RenamerStatus.SkipCollision,
        MoveOutcome.PermissionDenied => RenamerStatus.SkipPermissionDenied,
        MoveOutcome.VerifyFailed => RenamerStatus.SkipVerifyFailed,
        MoveOutcome.Cancelled => RenamerStatus.SkipCancelled,
        MoveOutcome.Moved => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            "A move that succeeded takes the planner's own status; ask only about one that did not happen."),
    };
}
