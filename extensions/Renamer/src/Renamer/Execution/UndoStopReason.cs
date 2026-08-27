namespace Renamer.Execution;

/// <summary>
/// Why one entry of a reverse replay stopped short of being restored, as a value rather than as prose.
/// </summary>
/// <remarks>
/// Each member names an arm that already exists on the undo path, and each is produced from the typed
/// outcome that arm already has — never parsed back out of the human-readable note that rides alongside
/// it. That note is for a person reading the undo panel; this is what
/// <see cref="UndoTerminalClassifier"/> reads to decide whether the entry's journal row may be retired.
/// <para>
/// <see cref="UnexpectedError"/> is the zero value deliberately. A reason that was never set therefore
/// classifies as retryable, so the failure mode of forgetting to assign one is a row that is offered
/// again — not a row that is silently retired and can never be recovered.
/// </para>
/// </remarks>
public enum UndoStopReason
{
    /// <summary>An unanticipated throw outside the save path, reported for that entry alone.</summary>
    UnexpectedError = 0,

    /// <summary>
    /// The row outlived its file: nothing in the library carries the id any more, so there is no current
    /// path to move back from. The one reason a later attempt cannot improve on.
    /// </summary>
    FileNoLongerInLibrary,

    /// <summary>The original directory is gone, or its volume is not mounted right now.</summary>
    OriginalDirectoryUnavailable,

    /// <summary>The original slot is taken on disk or in the database, and undo never clobbers.</summary>
    OriginalLocationOccupied,

    /// <summary>The reverse move found the source locked or the destination already present.</summary>
    ReverseMoveLockedOrTargetExists,

    /// <summary>The operating system refused the reverse move.</summary>
    ReverseMovePermissionDenied,

    /// <summary>A cross-volume reverse move's read-back did not match the source, so the copy was rejected.</summary>
    ReverseMoveVerifyFailed,

    /// <summary>The reverse move was cancelled mid-flight; the source was left untouched.</summary>
    ReverseMoveCancelled,

    /// <summary>The path recomputed after the save did not equal the restored path, so the move was rolled back.</summary>
    RestoredPathMismatch,

    /// <summary>The database save threw after a successful reverse move, which was then rolled back.</summary>
    DatabaseSaveFailed,
}

/// <summary>
/// Decides whether an entry that stopped short of being restored can still be retried, or never can.
/// </summary>
/// <remarks>
/// Pure by construction: no filesystem, no database, no host runtime, and no matching on the
/// human-readable note that accompanies a stop reason — that note is prose written for a person, and a
/// decision keyed on it changes meaning the moment someone rewords it.
/// <para>
/// EXACTLY ONE reason is terminal, and the asymmetry is a product judgement rather than a technical
/// one. Every other reason describes a condition the world can clear on its own or the owner can
/// correct: a lock is released, a drive is remounted, an occupied slot is
/// emptied. Keeping those rows pending costs nothing but a row, while retiring one wrongly removes the
/// only recovery path the user has for that file. The retention window sweeps whatever never resolves,
/// so erring this way cannot leak rows forever.
/// </para>
/// </remarks>
public static class UndoTerminalClassifier
{
    /// <summary>
    /// True when <paramref name="reason"/> can never be improved on by attempting the undo again, so
    /// the entry's journal row may be retired as unrestorable rather than offered as pending work.
    /// </summary>
    public static bool IsTerminal(UndoStopReason reason) => reason == UndoStopReason.FileNoLongerInLibrary;
}
