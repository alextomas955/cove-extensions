using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// The cross-volume data-loss proof. When the destination copy is corrupted (a flipped
/// byte) or torn (truncated) before verify, the verify FAILS, the result is
/// <see cref="CrossVolumeMover.MoveOutcome.VerifyFailed"/>, the SOURCE survives with its original
/// bytes, and the suspect destination + <c>.partial</c> are gone — an interrupted/corrupted transfer
/// never loses the original. The bit-flip case proves the content-hash half of verify; the truncation
/// case proves the size half. Both run entirely in a <see cref="TempDir"/> — no second physical drive
/// (a real two-drive run is a manual cross-platform check, deliberately NOT faked here). The corruption is
/// injected via the mover's test-only post-copy fault seam, keeping the production path clean.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class CrossVolumeVerifyFailTests
{
    private const string PartialSuffix = ".renamer-partial";

    [Fact]
    public async Task BitFlipDestination_VerifyFails_SourceSurvives_DestDeleted()
    {
        using var dir = new TempDir();
        const string original = "the real bytes that must survive";
        var src = dir.Touch("clip.mkv", original);
        var dest = Path.Combine(dir.Root, "moved", "clip.mkv");

        // Fault seam: flip exactly one byte of the .partial AFTER copy but BEFORE verify. The size is
        // unchanged, so this can only be caught by the content hash (not the size check).
        var mover = new CrossVolumeMover((partial, _) =>
        {
            var bytes = File.ReadAllBytes(partial);
            Assert.NotEmpty(bytes);
            bytes[0] ^= 0xFF; // flip one byte; length preserved
            File.WriteAllBytes(partial, bytes);
            return Task.CompletedTask;
        });

        var result = await mover.MoveAsync(src, dest, sidecars: null, CancellationToken.None);

        Assert.False(result.Moved);
        Assert.Equal(CrossVolumeMover.MoveOutcome.VerifyFailed, result.Outcome);
        Assert.True(File.Exists(src), "source MUST survive a failed verify");
        Assert.Equal(original, File.ReadAllText(src));
        Assert.False(File.Exists(dest), "the suspect destination must be deleted");
        Assert.False(File.Exists(dest + PartialSuffix), "no .partial left behind");
    }

    [Fact]
    public async Task TruncatedDestination_VerifyFails_SourceSurvives_DestDeleted()
    {
        using var dir = new TempDir();
        const string original = "the real bytes that must survive a torn write";
        var src = dir.Touch("clip.mkv", original);
        var dest = Path.Combine(dir.Root, "moved", "clip.mkv");

        // Fault seam: truncate the .partial to a SHORTER length (a torn/short write). This is caught by
        // the size half of verify independently of the hash.
        var mover = new CrossVolumeMover((partial, _) =>
        {
            var bytes = File.ReadAllBytes(partial);
            Assert.True(bytes.Length > 4, "test fixture must be longer than the truncation point");
            File.WriteAllBytes(partial, bytes[..4]); // shorter than the source
            return Task.CompletedTask;
        });

        var result = await mover.MoveAsync(src, dest, sidecars: null, CancellationToken.None);

        Assert.False(result.Moved);
        Assert.Equal(CrossVolumeMover.MoveOutcome.VerifyFailed, result.Outcome);
        Assert.True(File.Exists(src), "source MUST survive a truncated-dest verify failure");
        Assert.Equal(original, File.ReadAllText(src));
        Assert.False(File.Exists(dest), "the suspect (short) destination must be deleted");
        Assert.False(File.Exists(dest + PartialSuffix), "no .partial left behind");
    }
}
