using Renamer.Execution;

namespace Renamer.Tests.Execution;

/// <summary>
/// Pins the agreement between the length the planner reads and the segment
/// <see cref="CrossVolumeMover"/> actually appends.
/// </summary>
/// <remarks>
/// The preview's in-flight overflow warning derives its band from
/// <see cref="CrossVolumeMover.InFlightSuffixLength"/>, while the cost the executor really pays is
/// whatever the minter appends. Narrowing the minted segment without the declaration would leave the
/// warning firing on a band that no longer overruns, and a warning on a correct plan teaches a user to
/// ignore the warning.
/// <para>
/// The measured value comes from the minter, never from a recomposition of the marker and the character
/// count: a pin that rebuilt the string would agree with a rewritten minter forever. PURE - minting is
/// string arithmetic and touches no disk, so this needs no temp directory and none of the cross-volume
/// fixtures.
/// </para>
/// </remarks>
public sealed class InFlightSuffixLengthPinTests
{
    private const string FinalFull = "/dest/Film.mkv";

    [Fact]
    public void TheMintedSegment_IsAsLongAsTheDeclarationThePlannerReads()
    {
        string minted = CrossVolumeMover.MintInFlightPath(FinalFull);

        // The copy must land in the destination directory beside the final name, or the promote would stop
        // being a same-directory (atomic) rename - so the minted path EXTENDS the final one.
        Assert.StartsWith(FinalFull, minted, StringComparison.Ordinal);
        Assert.Equal(CrossVolumeMover.InFlightSuffixLength, minted.Length - FinalFull.Length);
    }

    [Fact]
    public void TwoMintsDifferInTheirTail_SoTheLengthAboveIsNotThatOfAFixedSuffix()
    {
        // Guards the reading of the first case: a minter that appended a FIXED suffix of the same length
        // would satisfy it just as well, and a fixed, guessable in-flight name is what the mover's
        // safety contract rules out.
        string first = CrossVolumeMover.MintInFlightPath(FinalFull);
        string second = CrossVolumeMover.MintInFlightPath(FinalFull);

        Assert.NotEqual(first, second);
        Assert.Equal(first.Length, second.Length);
    }
}
