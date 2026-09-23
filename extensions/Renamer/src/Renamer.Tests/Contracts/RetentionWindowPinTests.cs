using Renamer.Execution;

namespace Renamer.Tests.Contracts;

public sealed class RetentionWindowPinTests
{
    [Fact]
    public void RetentionWindowIsSevenDays_AndThePanelHoldsThatSameNumber()
    {
        Assert.True(
            (long)CoveRevertJournal.RetentionWindow.TotalMilliseconds == 604_800_000L,
            "The undo retention window moved. extensions/Renamer/src/Renamer.Ui/src/settings/"
                + "undoLogic.ts holds its own copy of this number (RETENTION_WINDOW_MS) so the "
                + "panel can state a batch's expiry date without a wire field for it - update that "
                + "constant, and the panel's suite, in the same change as this one.");
    }
}
