using Renamer.Planner;

namespace Renamer.Execution;

// How a primary move attempt was classified, shared by the same-volume DiskMover and the cross-volume
// CrossVolumeMover. Which members a tier can produce is that tier's own contract: an atomic
// same-volume rename has no copy to read back and no cancellation point, so DiskMover never returns
// VerifyFailed or Cancelled.
//
// The ordinals are pinned, so a new member is appended and never inserted.
public enum MoveOutcome
{
    // The file moved: on the same volume an atomic rename, across volumes a copy that was verified,
    // atomically promoted, and whose source was deleted last.
    Moved = 0,

    // The source was locked or in use, so the move never started: the source stays at its old path and
    // no destination was created.
    Locked,

    // The OS denied permission for the move, or across volumes for the copy, the promote or the source
    // delete.
    PermissionDenied,

    // The destination read-back did not match the source by size or content hash, so the copy was
    // rejected, the suspect destination deleted, and the source left intact.
    VerifyFailed,

    // The caller cancelled mid-move. The in-flight copy that call created is removed and the source is
    // left untouched, so a cancel never loses or duplicates a file and never throws out.
    Cancelled,

    // The destination path was occupied and is never overwritten. Both files survive: whatever holds
    // the destination name, and the source at its old path.
    //
    // Where the OS raises one IOException for an occupied destination and a locked source alike, the
    // catching tier tells them apart by testing the destination. The exception message is prose, and a
    // decision keyed on it changes meaning when someone rewords it.
    TargetExists,
}

// Translates a mover's classification of a move that did not happen into the status that move reports.
//
// The human-readable reason travelling beside an outcome is never matched on: it is prose, and a
// decision keyed on it changes meaning when someone rewords it.
//
// Every member is named and there is no discard arm, so the compiler refuses a new MoveOutcome member
// that has no status of its own. A discard arm would answer for members nobody considered, collapsing
// a lock, a denial, a failed verify and a clean shutdown into one status an operator cannot act on.
public static class MoveOutcomeClassifier
{
    // Throws for MoveOutcome.Moved: a move that happened takes the planner's own status for the item,
    // so there is nothing to translate.
    //
    // UndoReplayer.StopFor maps its own unreachable Moved arm to a retryable reason. There a wrong
    // answer retires a journal row and removes the user's only route back to their file, so it answers
    // harmlessly. Here nothing is retired and no recovery is at stake.
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
