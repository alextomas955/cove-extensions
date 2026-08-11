namespace Renamer.Execution;

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
/// correct: a lock is released, a drive is remounted, an allowlist is widened, an occupied slot is
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
