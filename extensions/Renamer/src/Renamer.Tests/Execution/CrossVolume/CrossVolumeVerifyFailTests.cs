using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.CrossVolume;

/// <summary>
/// The cross-volume data-loss proof. When the destination copy is corrupted (a flipped
/// byte) or torn (truncated) before verify, the verify FAILS, the result is
/// <see cref="CrossVolumeMover.MoveOutcome.VerifyFailed"/>, the SOURCE survives with its original
/// bytes, and the suspect destination and in-flight copy are gone — an interrupted/corrupted transfer
/// never loses the original. The bit-flip case proves the content-hash half of verify; the truncation
/// case proves the size half. Both run entirely in a <see cref="TempDir"/> — no second physical drive
/// (a real two-drive run is a manual cross-platform check, deliberately NOT faked here). The corruption is
/// injected via the mover's test-only post-copy fault seam, keeping the production path clean.
/// </summary>
/// <remarks>
/// That same seam is what tells each case WHICH path to assert on. The in-flight name is minted per
/// call and unguessable, so the alternative — reconstructing the expected path in the test — would be
/// asserting on a value the test supplied and would pass however wrong the real name was.
/// </remarks>
[Trait("Tier", "L1")]
public sealed class CrossVolumeVerifyFailTests
{
    [Fact]
    public async Task BitFlipDestination_VerifyFails_SourceSurvives_DestDeleted()
    {
        using var dir = new TempDir();
        const string original = "the real bytes that must survive";
        var src = dir.Touch("clip.mkv", original);
        var dest = Path.Combine(dir.Root, "moved", "clip.mkv");
        string? inFlight = null;

        // Fault seam: flip exactly one byte of the in-flight copy AFTER copy but BEFORE verify. The size
        // is unchanged, so this can only be caught by the content hash (not the size check). The seam
        // also hands over the minted path, which is how the leftover assertion below knows it.
        var mover = new CrossVolumeMover((path, _) =>
        {
            inFlight = path;
            var bytes = File.ReadAllBytes(path);
            Assert.NotEmpty(bytes);
            bytes[0] ^= 0xFF; // flip one byte; length preserved
            File.WriteAllBytes(path, bytes);
            return Task.CompletedTask;
        });

        var result = await mover.MoveAsync(src, dest, sidecars: null, CancellationToken.None);

        Assert.False(result.Moved);
        Assert.Equal(CrossVolumeMover.MoveOutcome.VerifyFailed, result.Outcome);
        Assert.True(File.Exists(src), "source MUST survive a failed verify");
        Assert.Equal(original, File.ReadAllText(src));
        Assert.False(File.Exists(dest), "the suspect destination must be deleted");
        Assert.NotNull(inFlight);
        Assert.False(File.Exists(inFlight), "no in-flight copy left behind");
    }

    [Fact]
    public async Task TruncatedDestination_VerifyFails_SourceSurvives_DestDeleted()
    {
        using var dir = new TempDir();
        const string original = "the real bytes that must survive a torn write";
        var src = dir.Touch("clip.mkv", original);
        var dest = Path.Combine(dir.Root, "moved", "clip.mkv");
        string? inFlight = null;

        // Fault seam: truncate the in-flight copy to a SHORTER length (a torn/short write). This is
        // caught by the size half of verify independently of the hash.
        var mover = new CrossVolumeMover((path, _) =>
        {
            inFlight = path;
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 4, "test fixture must be longer than the truncation point");
            File.WriteAllBytes(path, bytes[..4]); // shorter than the source
            return Task.CompletedTask;
        });

        var result = await mover.MoveAsync(src, dest, sidecars: null, CancellationToken.None);

        Assert.False(result.Moved);
        Assert.Equal(CrossVolumeMover.MoveOutcome.VerifyFailed, result.Outcome);
        Assert.True(File.Exists(src), "source MUST survive a truncated-dest verify failure");
        Assert.Equal(original, File.ReadAllText(src));
        Assert.False(File.Exists(dest), "the suspect (short) destination must be deleted");
        Assert.NotNull(inFlight);
        Assert.False(File.Exists(inFlight), "no in-flight copy left behind");
    }
}
