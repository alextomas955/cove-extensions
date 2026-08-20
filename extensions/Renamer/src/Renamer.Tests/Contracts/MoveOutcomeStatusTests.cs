using Renamer.Execution;
using Renamer.Planner;

namespace Renamer.Tests.Contracts;

/// <summary>
/// Pins WHICH status each mover outcome is reported under. No store, no database context, no
/// filesystem: the classifier touches none, which is why this suite needs no setup, no doubles and no
/// running service.
/// </summary>
/// <remarks>
/// PLACEMENT IS LOAD-BEARING, and this file is deliberately NOT under <c>Execution/</c>. The cove-absent
/// continuous-integration leg removes cove-dependent sources from those folders FILE BY FILE, so whether
/// a pure suite placed beside the code it covers keeps running there depends on a <c>Compile Remove</c>
/// entry nobody adds deliberately for a test that needs none — and this leg is where the repository's
/// coverage is thinnest. <c>Contracts/</c> is covered by no such entry at all, which is the guarantee.
/// Do not "tidy" this file next to <c>MoveOutcomeClassifier.cs</c>.
/// <para>
/// What this suite adds over the build is IDENTITY, and the distinction is the reason it exists at all.
/// The compiler already refuses a switch that omits a member, so totality is bought outright and needs
/// no test — but it is indifferent to WHICH status an outcome is paired with, and a mapping that sent
/// every outcome to the lock status would compile perfectly. That pairing is a decision, so the
/// expectations below are transcribed by hand from it; expectations derived from the classifier would
/// agree with the classifier forever, including about a swap nobody intended.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class MoveOutcomeStatusTests
{
    /// <summary>
    /// Every outcome a move that did NOT happen can carry, and the status it was deliberately given.
    /// Transcribed by hand from the decision, never generated from the enum.
    /// </summary>
    public static TheoryData<MoveOutcome, RenamerStatus> EveryNonMovedOutcome => new()
    {
        { MoveOutcome.LockedOrExists, RenamerStatus.SkipLocked },
        { MoveOutcome.PermissionDenied, RenamerStatus.SkipPermissionDenied },
        { MoveOutcome.VerifyFailed, RenamerStatus.SkipVerifyFailed },
        { MoveOutcome.Cancelled, RenamerStatus.SkipCancelled },
    };

    [Theory]
    [MemberData(nameof(EveryNonMovedOutcome))]
    public void EachOutcome_MapsToTheStatusTheTableSays(MoveOutcome outcome, RenamerStatus status) =>
        Assert.Equal(status, MoveOutcomeClassifier.StatusFor(outcome));

    [Fact]
    public void TheOneOutcomeWithNoStatus_Throws_SoItIsCoveredByAnAssertionRatherThanByAbsence()
    {
        // A move that happened takes the planner's own status, so `Moved` is the single member the
        // mapping answers for by refusing. Asserting the refusal keeps that arm from being the one
        // nothing describes — the state the deleted comment left this code in.
        var thrown = Assert.Throws<ArgumentOutOfRangeException>(
            () => MoveOutcomeClassifier.StatusFor(MoveOutcome.Moved));

        Assert.Equal("outcome", thrown.ParamName);
    }

    [Fact]
    public void EveryMemberOfTheTypeIsEitherTabledOrMoved_SoAnUntabledOneFailsRatherThanDefaults()
    {
        // What this covers that the compiler does not: the build refuses a SWITCH missing a member, but
        // nothing refuses a TEST missing a row. Without this, a member added later would compile only
        // once someone gave it an arm, and then be pinned by nobody.
        var tabled = EveryNonMovedOutcome.Select(row => (MoveOutcome)row[0]).ToHashSet();
        tabled.Add(MoveOutcome.Moved);

        Assert.Equal(Enum.GetValues<MoveOutcome>().ToHashSet(), tabled);
    }
}
