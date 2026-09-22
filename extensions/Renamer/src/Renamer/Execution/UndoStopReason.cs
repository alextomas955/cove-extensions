namespace Renamer.Execution;

// Why one entry of a reverse replay stopped short of being restored, as a value and not as prose. Each
// member is produced from the typed outcome its arm already has, and never parsed back out of the
// human-readable note beside it. UndoTerminalClassifier reads this to decide whether the entry's
// journal row may be retired.
//
// UnexpectedError is the zero value, so a reason that was never set classifies as retryable: forgetting
// to assign one offers the row again instead of silently retiring it.
public enum UndoStopReason
{
    // An unanticipated throw outside the save path, reported for that entry alone.
    UnexpectedError = 0,

    // The row outlived its file: nothing in the library carries the id any more, so there is no current
    // path to move back from. The one reason a later attempt cannot improve on.
    FileNoLongerInLibrary,

    // The original directory is gone, or its volume is not mounted right now.
    OriginalDirectoryUnavailable,

    // The original slot is taken on disk or in the database, and undo never clobbers.
    OriginalLocationOccupied,

    // The reverse move found the source locked or the destination already present.
    ReverseMoveLockedOrTargetExists,

    // The operating system refused the reverse move.
    ReverseMovePermissionDenied,

    // A cross-volume reverse move's read-back did not match the source, so the copy was rejected.
    ReverseMoveVerifyFailed,

    // The reverse move was cancelled mid-flight; the source was left untouched.
    ReverseMoveCancelled,

    // The path recomputed after the save did not equal the restored path, so the move was rolled back.
    RestoredPathMismatch,

    // The save returned no row for this file, so the restored path could not be checked at all and the
    // move was rolled back. Distinct from RestoredPathMismatch because nothing was compared: reporting
    // a mismatch would name a path the save never reported.
    SaveReportedNoRow,

    // The database save threw after a successful reverse move, which was then rolled back.
    DatabaseSaveFailed,
}

// Decides whether an entry that stopped short of being restored can still be retried.
//
// Exactly one reason is terminal. Every other reason describes a condition the world can clear or the
// owner can correct: a lock is released, a drive is remounted, an occupied slot is emptied. Keeping
// those rows pending costs a row, while retiring one wrongly removes the user's only recovery path for
// that file. The retention window sweeps whatever never resolves.
public static class UndoTerminalClassifier
{
    // True when no later attempt can improve on the reason, so the entry's journal row may be retired
    // as unrestorable instead of offered as pending work.
    public static bool IsTerminal(UndoStopReason reason) => reason == UndoStopReason.FileNoLongerInLibrary;
}
