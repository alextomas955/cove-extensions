using Renamer.Planner;

namespace Renamer.Execution;

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
        MoveOutcome.LockedOrExists => RenamerStatus.SkipLocked,
        MoveOutcome.PermissionDenied => RenamerStatus.SkipPermissionDenied,
        MoveOutcome.VerifyFailed => RenamerStatus.SkipVerifyFailed,
        MoveOutcome.Cancelled => RenamerStatus.SkipCancelled,
        MoveOutcome.Moved => throw new ArgumentOutOfRangeException(
            nameof(outcome), outcome,
            "A move that succeeded takes the planner's own status; ask only about one that did not happen."),
    };
}
