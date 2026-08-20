using Renamer.Execution;

namespace Renamer.Tests.Contracts;

/// <summary>
/// Pins which reasons for an undo entry stopping are terminal — a row retired as unrestorable — and
/// which stay pending to be retried. No store, no database context, no filesystem: the classifier
/// touches none, which is why this suite needs no setup, no doubles and no running service.
/// </summary>
/// <remarks>
/// PLACEMENT IS LOAD-BEARING, and this file is deliberately NOT under <c>Execution/</c>. The
/// cove-absent continuous-integration leg removes the <c>Execution</c>, <c>Events</c>,
/// <c>Concurrency</c> and <c>Preview</c> folders wholesale, so a pure suite placed beside the code it
/// covers would silently stop running exactly on the leg where this repository's coverage is thinnest.
/// <c>Contracts/</c> is covered by no <c>Compile Remove</c> entry. Do not "tidy" this file next to
/// <c>UndoTerminalClassifier.cs</c>.
/// <para>
/// The expectations are a hand-written table, and a separate case checks that table against the type's
/// own members. That pairing is the point: a member added later without a deliberate entry here fails
/// the suite instead of quietly inheriting whatever the classifier happens to return for it. A test
/// that derived its expectations from the classifier would agree with it forever.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class UndoTerminalClassifierTests
{
    /// <summary>
    /// Every stop reason and the classification it was deliberately given: true is terminal, false is
    /// retried. Transcribed by hand from the decision, never generated from the enum.
    /// </summary>
    public static TheoryData<UndoStopReason, bool> EveryStopReason => new()
    {
        { UndoStopReason.UnexpectedError, false },
        { UndoStopReason.FileNoLongerInLibrary, true },
        { UndoStopReason.RestoreTargetRejectedByAllowlist, false },
        { UndoStopReason.OriginalDirectoryUnavailable, false },
        { UndoStopReason.OriginalLocationOccupied, false },
        { UndoStopReason.ReverseMoveLockedOrTargetExists, false },
        { UndoStopReason.ReverseMovePermissionDenied, false },
        { UndoStopReason.ReverseMoveVerifyFailed, false },
        { UndoStopReason.ReverseMoveCancelled, false },
        { UndoStopReason.RestoredPathMismatch, false },
        { UndoStopReason.DatabaseSaveFailed, false },
    };

    [Theory]
    [MemberData(nameof(EveryStopReason))]
    public void EachStopReason_ClassifiesAsTheTableSays(UndoStopReason reason, bool terminal) =>
        Assert.Equal(terminal, UndoTerminalClassifier.IsTerminal(reason));

    [Fact]
    public void EveryMemberOfTheTypeAppearsInTheTable_SoAnUnclassifiedOneFailsRatherThanDefaults()
    {
        var classified = EveryStopReason.Select(row => (UndoStopReason)row[0]!).ToHashSet();

        Assert.Equal(Enum.GetValues<UndoStopReason>().ToHashSet(), classified);
    }

    [Fact]
    public void ExactlyOneReasonIsTerminal_AndItIsTheFileLeavingTheLibrary()
    {
        // The asymmetry IS the safety property: a reason wrongly called terminal retires the row that
        // holds the user's only route back to their file, while a reason wrongly called retryable costs
        // one row that the retention window sweeps anyway.
        var terminal = Enum.GetValues<UndoStopReason>().Where(UndoTerminalClassifier.IsTerminal);

        Assert.Equal([UndoStopReason.FileNoLongerInLibrary], terminal);
    }

    [Fact]
    public void TheDefaultValue_IsRetryable_SoAnUnsetReasonNeverRetiresARow()
    {
        // A reason nobody assigned must not be the one that deletes a row for good.
        Assert.False(UndoTerminalClassifier.IsTerminal(default));
    }
}
