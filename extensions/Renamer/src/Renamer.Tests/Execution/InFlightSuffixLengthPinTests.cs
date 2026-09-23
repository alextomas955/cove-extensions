using Renamer.Execution;
using Renamer.Planner;

namespace Renamer.Tests.Execution;

public sealed class InFlightSuffixLengthPinTests
{
    private const string FinalFull = "/dest/Film.mkv";

    [Fact]
    public void TheMintedSegment_IsAsLongAsTheDeclarationThePlannerReads()
    {
        string minted = CrossVolumeMover.MintInFlightPath(FinalFull);

        // The copy must land in the destination directory beside the final name, or the promote would stop
        // being a same-directory (atomic) rename - so the minted path extends the final one.
        Assert.StartsWith(FinalFull, minted, StringComparison.Ordinal);
        Assert.Equal(PathOps.InFlightSuffixLength, minted.Length - FinalFull.Length);
    }

    [Fact]
    public void TwoMintsDifferInTheirTail_SoTheLengthAboveIsNotThatOfAFixedSuffix()
    {
        // Guards the reading of the first case: a minter that appended a fixed suffix of the same length
        // would satisfy it just as well, and a fixed, guessable in-flight name is what the mover's
        // safety contract rules out.
        string first = CrossVolumeMover.MintInFlightPath(FinalFull);
        string second = CrossVolumeMover.MintInFlightPath(FinalFull);

        Assert.NotEqual(first, second);
        Assert.Equal(first.Length, second.Length);
    }
}
