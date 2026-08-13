using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.CrossVolume;

/// <summary>
/// The regression for the silent data loss this design removed: the mover used to derive one fixed,
/// guessable in-flight path from the destination and delete it before every copy, so a user's own file
/// sitting at that name was destroyed without a word — valid data the code had no way to prove was a
/// stale artifact.
/// </summary>
/// <remarks>
/// PROPERTY PINNED: a file already present in the destination directory is untouched by a
/// cross-volume move, whether the move succeeds or its verify fails. The mover deletes only paths it
/// minted inside that same call.
/// <para>
/// Be honest about what a random name lets a test assert. Because the in-flight name is minted per
/// call from a cryptographic random source, no fixed path can be pre-planted to collide with the next
/// one — so this suite cannot demonstrate a near-miss, and does not pretend to. What it CAN pin is the
/// property that matters to a user: whatever is already sitting in the destination directory, the move
/// leaves it alone. The planted files are named in the shape a minted in-flight file takes, so this
/// would have failed loudly against the fixed-suffix code it replaces.
/// </para>
/// <para>
/// The minted path itself is learned from the mover through the post-copy seam, never constructed
/// here, so the "only what it minted was removed" half of each case asserts on a value production
/// supplied rather than one the test made up.
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
public sealed class CrossVolumeTempOwnershipTests
{
    private const string UserContent = "a user's own file, valid data the mover has no claim on";

    [Fact]
    public async Task UserFileInDestinationDirectory_SurvivesASuccessfulMove_Intact()
    {
        using var dir = new TempDir();
        var src = dir.Touch("clip.mkv", "the bytes being moved");
        var dest = Path.Combine(dir.Root, "moved", "Renamed.mkv");

        // Two files the user owns, both in the destination directory, both shaped like an in-flight
        // copy: one hung off the exact final path the move targets, one off a different final name.
        var plantedOnTarget = dir.Touch("moved/Renamed.mkv.rnm0cf1c5d4", UserContent);
        var plantedElsewhere = dir.Touch("moved/Holiday.mkv.rnmc15381a0", UserContent);

        var minted = new List<string>();
        var mover = new CrossVolumeMover(Recorder(minted));

        var result = await mover.MoveAsync(src, dest, sidecars: null, CancellationToken.None);

        Assert.True(result.Moved);
        Assert.Equal("the bytes being moved", File.ReadAllText(dest));

        AssertUserFilesIntact(plantedOnTarget, plantedElsewhere);
        AssertOnlyMintedPathsWereRemoved(minted, plantedOnTarget, plantedElsewhere);
    }

    [Fact]
    public async Task UserFileInDestinationDirectory_SurvivesAVerifyFailure_WhileOnlyTheMintedCopyIsRemoved()
    {
        using var dir = new TempDir();
        const string original = "the bytes that must survive a failed verify";
        var src = dir.Touch("clip.mkv", original);
        var dest = Path.Combine(dir.Root, "moved", "Renamed.mkv");

        var plantedOnTarget = dir.Touch("moved/Renamed.mkv.rnm0cf1c5d4", UserContent);
        var plantedElsewhere = dir.Touch("moved/Holiday.mkv.rnmc15381a0", UserContent);

        // Corrupt the in-flight copy between copy and verify, so the failure arm — the one that DOES
        // delete — runs. It must reach the minted path and nothing else.
        var minted = new List<string>();
        var mover = new CrossVolumeMover((inFlight, _) =>
        {
            minted.Add(inFlight);
            var bytes = File.ReadAllBytes(inFlight);
            bytes[0] ^= 0xFF;
            File.WriteAllBytes(inFlight, bytes);
            return Task.CompletedTask;
        });

        var result = await mover.MoveAsync(src, dest, sidecars: null, CancellationToken.None);

        Assert.False(result.Moved);
        Assert.Equal(MoveOutcome.VerifyFailed, result.Outcome);
        Assert.True(File.Exists(src), "the source must survive a failed verify");
        Assert.Equal(original, File.ReadAllText(src));
        Assert.False(File.Exists(dest), "the suspect destination must not be promoted");

        AssertUserFilesIntact(plantedOnTarget, plantedElsewhere);
        AssertOnlyMintedPathsWereRemoved(minted, plantedOnTarget, plantedElsewhere);
    }

    private static void AssertUserFilesIntact(params string[] planted)
    {
        foreach (var path in planted)
        {
            Assert.True(File.Exists(path), $"the mover deleted a file it did not create: {path}");
            Assert.Equal(UserContent, File.ReadAllText(path));
        }
    }

    /// <summary>
    /// The other half of the ownership claim: what the mover DID mint is gone, so the planted files
    /// surviving is not merely the mover having deleted nothing at all.
    /// </summary>
    private static void AssertOnlyMintedPathsWereRemoved(List<string> minted, params string[] planted)
    {
        Assert.NotEmpty(minted); // the seam must have fired, or the loop below asserts nothing
        foreach (var path in minted)
        {
            Assert.DoesNotContain(path, planted);
            Assert.False(File.Exists(path), $"the in-flight copy the mover minted must be gone: {path}");
        }
    }

    private static Func<string, CancellationToken, Task> Recorder(List<string> minted) =>
        (inFlight, _) =>
        {
            minted.Add(inFlight);
            return Task.CompletedTask;
        };
}
