using Renamer.Execution;

namespace Renamer.Tests.Execution;

public sealed class UndoTerminalClassifierTests
{
    [Fact]
    public void ExactlyOneReasonIsTerminal_AndItIsTheFileLeavingTheLibrary()
    {
        // The asymmetry is the safety property: a reason wrongly called terminal retires the row that
        // holds the user's only route back to their file, while a reason wrongly called retryable costs
        // one row that the retention window sweeps anyway.
        var terminal = Enum.GetValues<UndoStopReason>().Where(UndoTerminalClassifier.IsTerminal);

        Assert.Equal([UndoStopReason.FileNoLongerInLibrary], terminal);
    }

    [Fact]
    public void TheDefaultValue_IsRetryable_SoAnUnsetReasonNeverRetiresARow() =>
        Assert.False(UndoTerminalClassifier.IsTerminal(default));
}
