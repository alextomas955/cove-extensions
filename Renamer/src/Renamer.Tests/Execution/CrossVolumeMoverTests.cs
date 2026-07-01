using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// The cross-volume copy → verify(size + hash) → atomic-renamer → delete-source-last primitive,
/// exercised DIRECTLY against the real filesystem via the <see cref="TempDir"/> fixture (no second
/// physical drive — the mover is called regardless of the real volume layout, exactly like
/// <see cref="DiskMover"/>'s tests). Proves: a verified happy move; no-clobber on an existing dest;
/// a same-size-but-different-content copy is rejected (size-only would false-pass); a locked source
/// is a classified skip not a throw; sidecars skip-not-clobber; a stale <c>.partial</c> is never
/// promoted unverified.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class CrossVolumeMoverTests
{
    private const string PartialSuffix = ".renamer-partial";

    [Fact]
    public async Task HappyMove_RealTempDir_DestHasContent_SourceGone_NoPartial()
    {
        using var dir = new TempDir();
        var old = dir.Touch("clip.mkv", "hello bytes");
        var dest = Path.Combine(dir.Root, "sub", "Renamed.mkv");
        var mover = new CrossVolumeMover();

        var result = await mover.MoveAsync(old, dest, sidecars: null, CancellationToken.None);

        Assert.True(result.Moved);
        Assert.Equal(CrossVolumeMover.MoveOutcome.Moved, result.Outcome);
        Assert.True(File.Exists(dest), "dest must exist after a verified promote (parent dir auto-created)");
        Assert.Equal("hello bytes", File.ReadAllText(dest));
        Assert.False(File.Exists(old), "source must be deleted only AFTER the verified promote");
        Assert.False(File.Exists(dest + PartialSuffix), "no .partial must remain after a successful move");
    }

    [Fact]
    public async Task DestExists_NoClobber_PreservedAndSourceSurvives()
    {
        using var dir = new TempDir();
        var old = dir.Touch("clip.mkv", "new bytes");
        var dest = dir.Touch("Taken.mkv", "original");
        var mover = new CrossVolumeMover();

        var result = await mover.MoveAsync(old, dest, sidecars: null, CancellationToken.None);

        Assert.False(result.Moved);
        Assert.Equal(CrossVolumeMover.MoveOutcome.LockedOrExists, result.Outcome);
        // The pre-existing destination is never clobbered, and the source survives.
        Assert.Equal("original", File.ReadAllText(dest));
        Assert.True(File.Exists(old));
        Assert.Equal("new bytes", File.ReadAllText(old));
        Assert.False(File.Exists(dest + PartialSuffix), "no .partial must remain on a no-clobber skip");
    }

    [Fact]
    public async Task SizeEqualHashDiffers_VerifyFailed_SourceSurvives_DestDeleted()
    {
        using var dir = new TempDir();
        const string original = "the original content";
        var old = dir.Touch("clip.mkv", original);
        var dest = Path.Combine(dir.Root, "moved", "clip.mkv");

        // Fault seam: between copy and verify, rewrite the .partial to the SAME length but DIFFERENT
        // bytes. A size-only verify would false-pass; the hash must catch this (MOVE-03).
        var mover = new CrossVolumeMover((partial, _) =>
        {
            var corrupt = new string('Z', original.Length);
            Assert.Equal(original.Length, corrupt.Length); // same size, different content
            File.WriteAllText(partial, corrupt);
            return Task.CompletedTask;
        });

        var result = await mover.MoveAsync(old, dest, sidecars: null, CancellationToken.None);

        Assert.False(result.Moved);
        Assert.Equal(CrossVolumeMover.MoveOutcome.VerifyFailed, result.Outcome);
        Assert.True(File.Exists(old), "source MUST survive a size-equal-hash-differs verify failure");
        Assert.Equal(original, File.ReadAllText(old));
        Assert.False(File.Exists(dest), "the suspect destination must be deleted");
        Assert.False(File.Exists(dest + PartialSuffix), "no .partial must remain after a verify failure");
    }

    [Fact]
    public async Task LockedSource_Skipped_NotThrown_SourceIntact()
    {
        using var dir = new TempDir();
        var old = dir.Touch("clip.mkv", "data");
        var dest = Path.Combine(dir.Root, "sub", "Renamed.mkv");
        var mover = new CrossVolumeMover();

        // Hold the SOURCE open exclusively so the copy's source FileStream throws IOException.
        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await mover.MoveAsync(old, dest, sidecars: null, CancellationToken.None);

            Assert.False(result.Moved);
            Assert.Equal(CrossVolumeMover.MoveOutcome.LockedOrExists, result.Outcome);
            Assert.NotNull(result.Reason);
        }

        Assert.True(File.Exists(old), "locked source must remain at its old path");
        Assert.Equal("data", File.ReadAllText(old));
        Assert.False(File.Exists(dest), "no destination must be created when the source is locked");
        Assert.False(File.Exists(dest + PartialSuffix), "no .partial must remain when the source is locked");
    }

    [Fact]
    public async Task SidecarSkipNotClobber_PrimaryMoves_ExistingSidecarTargetUntouched()
    {
        using var dir = new TempDir();
        var old = dir.Touch("clip.mkv", "primary bytes");
        var dest = Path.Combine(dir.Root, "moved", "clip.mkv");

        // One sidecar whose target is free (should move) and one whose target already exists (skip).
        var freeFrom = dir.Touch("clip.en.srt", "free sidecar");
        var freeTo = Path.Combine(dir.Root, "moved", "clip.en.srt");
        var takenFrom = dir.Touch("clip.fr.srt", "taken sidecar source");
        var takenTo = dir.Touch("moved/clip.fr.srt", "pre-existing sidecar");

        var sidecars = new List<CrossVolumeMover.SidecarMove>
        {
            new(freeFrom, freeTo),
            new(takenFrom, takenTo),
        };
        var mover = new CrossVolumeMover();

        var result = await mover.MoveAsync(old, dest, sidecars, CancellationToken.None);

        Assert.True(result.Moved);
        Assert.Equal(CrossVolumeMover.MoveOutcome.Moved, result.Outcome);
        Assert.Equal("primary bytes", File.ReadAllText(dest));
        Assert.False(File.Exists(old));

        // The free sidecar followed copy→verify→delete and is recorded for rollback.
        Assert.Contains(result.MovedSidecars, s => s.From == freeFrom && s.To == freeTo);
        Assert.Equal("free sidecar", File.ReadAllText(freeTo));
        Assert.False(File.Exists(freeFrom));

        // The taken sidecar target is left untouched (skip-not-clobber) and a warning was recorded.
        Assert.Equal("pre-existing sidecar", File.ReadAllText(takenTo));
        Assert.True(File.Exists(takenFrom), "a skipped sidecar's source must be left in place");
        Assert.DoesNotContain(result.MovedSidecars, s => s.From == takenFrom);
        Assert.Contains(result.Warnings, w => w.Contains(takenTo));
    }

    [Fact]
    public async Task CancelledToken_ClassifiedSkip_NotThrown_SourceIntact_NoPartial()
    {
        using var dir = new TempDir();
        var old = dir.Touch("clip.mkv", "the bytes that must survive a cancel");
        var dest = Path.Combine(dir.Root, "moved", "clip.mkv");
        var mover = new CrossVolumeMover();

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancelled BEFORE the copy's first ReadAsync → OperationCanceledException

        // classify-not-throw: a cancel must NOT escape MoveAsync (the executor relies on the mover
        // never throwing out). It must return a classified Cancelled skip.
        var result = await mover.MoveAsync(old, dest, sidecars: null, cts.Token);

        Assert.False(result.Moved);
        Assert.Equal(CrossVolumeMover.MoveOutcome.Cancelled, result.Outcome);
        Assert.NotNull(result.Reason);
        Assert.True(File.Exists(old), "a cancelled move must leave the source untouched");
        Assert.Equal("the bytes that must survive a cancel", File.ReadAllText(old));
        Assert.False(File.Exists(dest), "no destination must be promoted on a cancel");
        Assert.False(File.Exists(dest + PartialSuffix), "the in-flight .partial must be cleaned up on a cancel");
    }

    [Fact]
    public async Task LeftoverPartial_NeverPromoted_FreshVerifiedFinalProduced()
    {
        using var dir = new TempDir();
        var old = dir.Touch("clip.mkv", "the genuine bytes");
        var dest = Path.Combine(dir.Root, "moved", "clip.mkv");

        // A stale, UNVERIFIED .partial left from a crashed prior run.
        var stalePartial = dest + PartialSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(stalePartial)!);
        File.WriteAllText(stalePartial, "STALE UNVERIFIED GARBAGE - must never be promoted");

        var mover = new CrossVolumeMover();
        var result = await mover.MoveAsync(old, dest, sidecars: null, CancellationToken.None);

        Assert.True(result.Moved);
        Assert.Equal(CrossVolumeMover.MoveOutcome.Moved, result.Outcome);
        // The final is the FRESH verified copy of the source, never the stale .partial's contents.
        Assert.Equal("the genuine bytes", File.ReadAllText(dest));
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(stalePartial), "the stale .partial must be cleaned, never promoted");
    }
}
