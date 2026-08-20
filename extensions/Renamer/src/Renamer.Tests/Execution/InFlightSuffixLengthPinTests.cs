using Renamer.Execution;

namespace Renamer.Tests.Execution;

/// <summary>
/// Pins the agreement between the length the planner reads and the segment
/// <see cref="CrossVolumeMover"/> actually appends.
/// </summary>
/// <remarks>
/// Two sides that must not drift. The preview's in-flight overflow warning derives its band from
/// <see cref="CrossVolumeMover.InFlightSuffixLength"/>, while the cost the executor really pays is
/// whatever the minter appends. The minted segment has already been narrowed once — a 16-character
/// fixed suffix became <c>".rnm"</c> plus 8 hexadecimal characters — so a further
/// narrowing is a live possibility rather than a hypothetical, and it would leave the warning firing on a
/// band that no longer overruns. A warning on a correct plan is worse than the silence it replaced,
/// because a user who is told to shorten a name that already fits learns to ignore the warning.
/// <para>
/// The measured value comes from the minter, never from a recomposition of the marker and the character
/// count: a pin that rebuilt the string would agree with a rewritten minter forever, which is exactly the
/// blindness it exists to avoid. PURE — minting is string arithmetic and touches no disk, so this needs
/// no temp directory and none of the <c>L1</c> cross-volume fixtures.
/// </para>
/// </remarks>
[Trait("Tier", "L0")]
public sealed class InFlightSuffixLengthPinTests
{
    private const string FinalFull = "/dest/Film.mkv";

    [Fact]
    public void TheMintedSegment_IsAsLongAsTheDeclarationThePlannerReads()
    {
        string minted = CrossVolumeMover.MintInFlightPath(FinalFull);

        // The copy must land in the destination directory beside the final name, or the promote would stop
        // being a same-directory (atomic) rename — so the minted path EXTENDS the final one.
        Assert.StartsWith(FinalFull, minted, StringComparison.Ordinal);
        Assert.Equal(CrossVolumeMover.InFlightSuffixLength, minted.Length - FinalFull.Length);
    }

    // A third case used to assert the declared length equals a hand-written 12. It was an
    // internal-constant pin — a number checked against a copy of itself, with no second component
    // involved — so it could only ever fail on a deliberate edit to the declaration, which is not a
    // defect class. The two cases here are cross-component and stay: one holds the MINTER and the
    // declaration to each other, the other holds the mint to being genuinely variable.

    [Fact]
    public void TwoMintsDifferInTheirTail_SoTheLengthAboveIsNotThatOfAFixedSuffix()
    {
        // Guards the reading of the first case: a minter that appended a FIXED twelve characters would
        // satisfy it just as well, and a fixed, guessable name is precisely what the mover's safety
        // contract was rewritten to remove.
        string first = CrossVolumeMover.MintInFlightPath(FinalFull);
        string second = CrossVolumeMover.MintInFlightPath(FinalFull);

        Assert.NotEqual(first, second);
        Assert.Equal(first.Length, second.Length);
    }
}
