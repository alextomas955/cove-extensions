using Renamer.Execution;

namespace Renamer.Tests.Contracts;

/// <summary>
/// The cross-language pin on the undo retention window.
/// </summary>
/// <remarks>
/// The undo panel states the batch's actual expiry date, computed from the open timestamp the
/// <c>/last-batch</c> summary already carries plus the window - and the window deliberately gets no
/// wire field of its own, because it is a constant rather than per-batch data. That decision buys a
/// smaller wire surface and costs one duplicated number: the panel holds its own copy in
/// <c>Renamer.Ui/src/settings/undoLogic.ts</c>.
/// <para>
/// A duplicated number with nothing watching it drifts silently, and the symptom would be a date the
/// user trusts and the server does not honour. So the duplication is pinned here rather than
/// commented: the expectation below is transcribed by hand, never read from
/// <see cref="CoveRevertJournal.RetentionWindow"/> through arithmetic that would agree with it forever,
/// and the failure message names the file that has to move with it.
/// </para>
/// </remarks>
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
