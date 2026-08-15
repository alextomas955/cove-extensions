using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.CrossVolume;

/// <summary>
/// The cross-volume copy → verify(size + hash) → atomic-renamer → delete-source-last primitive,
/// exercised DIRECTLY against the real filesystem via the <see cref="TempDir"/> fixture (no second
/// physical drive — the mover is called regardless of the real volume layout, exactly like
/// <see cref="DiskMover"/>'s tests). Proves: a verified happy move; no-clobber on an existing dest;
/// a same-size-but-different-content copy is rejected (size-only would false-pass); a locked source
/// is a classified skip not a throw; sidecars skip-not-clobber; an in-flight copy orphaned by an
/// earlier crash is left in place and never promoted unverified; and two moves to the same final
/// path mint different in-flight names.
/// </summary>
/// <remarks>
/// The in-flight name is minted per call and unguessable, so these cases LEARN it from the mover
/// through the post-copy seam rather than constructing it. That direction is the point: a test that
/// built its own expected path would be asserting on a value it supplied itself and would keep
/// passing however wrong the real name was — which is exactly what the suite did before the name was
/// minted. Where the copy never gets far enough to reach the seam (a no-clobber skip, a locked
/// source, a cancel), the case asserts on the destination directory's whole contents instead, which
/// needs no name at all.
/// </remarks>
[Trait("Tier", "L1")]
public sealed class CrossVolumeMoverTests
{
    [Fact]
    public async Task HappyMove_RealTempDir_DestHasContent_SourceGone_NoInFlightCopy()
    {
        using var dir = new TempDir();
        var old = dir.Touch("clip.mkv", "hello bytes");
        var dest = Path.Combine(dir.Root, "sub", "Renamed.mkv");
        var minted = new List<string>();
        var mover = new CrossVolumeMover(Recorder(minted));

        var result = await mover.MoveAsync(old, dest, sidecars: null, CancellationToken.None);

        Assert.True(result.Moved);
        Assert.Equal(MoveOutcome.Moved, result.Outcome);
        Assert.True(File.Exists(dest), "dest must exist after a verified promote (parent dir auto-created)");
        Assert.Equal("hello bytes", File.ReadAllText(dest));
        Assert.False(File.Exists(old), "source must be deleted only AFTER the verified promote");
        AssertMintedPathsGone(minted, "the promoted in-flight copy must not also remain at its minted name");
        Assert.Equal(new[] { dest }, Directory.GetFiles(Path.GetDirectoryName(dest)!));
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
        Assert.Equal(MoveOutcome.LockedOrExists, result.Outcome);
        // The pre-existing destination is never clobbered, and the source survives.
        Assert.Equal("original", File.ReadAllText(dest));
        Assert.True(File.Exists(old));
        Assert.Equal("new bytes", File.ReadAllText(old));
        // The no-clobber pre-check returns before anything is minted, so the directory holds exactly
        // the two files the test put there.
        Assert.Equal(new[] { dest, old }.Order(), Directory.GetFiles(dir.Root).Order());
    }

    [Fact]
    public async Task SizeEqualHashDiffers_VerifyFailed_SourceSurvives_DestDeleted()
    {
        using var dir = new TempDir();
        const string original = "the original content";
        var old = dir.Touch("clip.mkv", original);
        var dest = Path.Combine(dir.Root, "moved", "clip.mkv");
        var minted = new List<string>();

        // Fault seam: between copy and verify, rewrite the in-flight copy to the SAME length but
        // DIFFERENT bytes. A size-only verify would false-pass; the hash must catch this. The seam also
        // hands the test the minted path, which is the only way to know it.
        var mover = new CrossVolumeMover((inFlight, _) =>
        {
            minted.Add(inFlight);
            var corrupt = new string('Z', original.Length);
            Assert.Equal(original.Length, corrupt.Length); // same size, different content
            File.WriteAllText(inFlight, corrupt);
            return Task.CompletedTask;
        });

        var result = await mover.MoveAsync(old, dest, sidecars: null, CancellationToken.None);

        Assert.False(result.Moved);
        Assert.Equal(MoveOutcome.VerifyFailed, result.Outcome);
        Assert.True(File.Exists(old), "source MUST survive a size-equal-hash-differs verify failure");
        Assert.Equal(original, File.ReadAllText(old));
        Assert.False(File.Exists(dest), "the suspect destination must be deleted");
        AssertMintedPathsGone(minted, "the rejected in-flight copy must be removed after a verify failure");
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
            Assert.Equal(MoveOutcome.LockedOrExists, result.Outcome);
            Assert.NotNull(result.Reason);
        }

        Assert.True(File.Exists(old), "locked source must remain at its old path");
        Assert.Equal("data", File.ReadAllText(old));
        Assert.False(File.Exists(dest), "no destination must be created when the source is locked");
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(dest)!));
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
        Assert.Equal(MoveOutcome.Moved, result.Outcome);
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
    public async Task CancelledToken_ClassifiedSkip_NotThrown_SourceIntact_NoInFlightCopy()
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
        Assert.Equal(MoveOutcome.Cancelled, result.Outcome);
        Assert.NotNull(result.Reason);
        Assert.True(File.Exists(old), "a cancelled move must leave the source untouched");
        Assert.Equal("the bytes that must survive a cancel", File.ReadAllText(old));
        Assert.False(File.Exists(dest), "no destination must be promoted on a cancel");
        // The cancel throws out of the read loop AFTER the in-flight file was opened CreateNew, so an
        // empty directory here is the proof it was removed. The seam is never reached on this path.
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(dest)!));
    }

    [Fact]
    public async Task CrashOrphanedInFlightCopy_LeftInPlace_NeverPromoted_FreshVerifiedFinalProduced()
    {
        using var dir = new TempDir();
        var old = dir.Touch("clip.mkv", "the genuine bytes");
        var dest = Path.Combine(dir.Root, "moved", "clip.mkv");

        // An unverified in-flight copy orphaned by a crashed prior run, at a name that run minted.
        const string orphanContent = "ORPHANED UNVERIFIED BYTES - must never be promoted";
        var orphan = dest + ".rnm0cf1c5d4";
        Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
        File.WriteAllText(orphan, orphanContent);

        var minted = new List<string>();
        var mover = new CrossVolumeMover(Recorder(minted));
        var result = await mover.MoveAsync(old, dest, sidecars: null, CancellationToken.None);

        Assert.True(result.Moved);
        Assert.Equal(MoveOutcome.Moved, result.Outcome);
        // The final is the FRESH verified copy of the source, never the orphan's contents.
        Assert.Equal("the genuine bytes", File.ReadAllText(dest));
        Assert.False(File.Exists(old));
        // The orphan is inert, not garbage to collect: this call minted a different name, so it is
        // neither promoted nor collided with — and the mover deletes only what it created.
        Assert.DoesNotContain(orphan, minted);
        Assert.True(File.Exists(orphan), "an orphan the mover did not create must be left alone");
        Assert.Equal(orphanContent, File.ReadAllText(orphan));
    }

    /// <summary>
    /// The two properties the mint itself has to carry: a second move to the SAME final path takes a
    /// different in-flight name (which is what makes an orphan harmless without a sweep), and the
    /// minted segment is no longer than the 16-character fixed suffix it replaced (the planner budgets
    /// only the final path, so a longer name would widen an already-unbudgeted gap).
    /// </summary>
    [Fact]
    public async Task TwoMovesToTheSameFinalPath_MintDifferentInFlightNames_AndNoneExceedSixteenCharacters()
    {
        using var dir = new TempDir();
        var dest = Path.Combine(dir.Root, "moved", "clip.mkv");
        var minted = new List<string>();
        var mover = new CrossVolumeMover(Recorder(minted));

        for (int i = 0; i < 2; i++)
        {
            var src = dir.Touch($"clip{i}.mkv", $"bytes {i}");
            var result = await mover.MoveAsync(src, dest, sidecars: null, CancellationToken.None);
            Assert.True(result.Moved);
            // Clear the final so the second attempt reaches the copy rather than the no-clobber
            // pre-check, which would return before minting anything.
            File.Delete(dest);
        }

        Assert.Equal(2, minted.Count);
        Assert.Distinct(minted);
        foreach (var path in minted)
        {
            Assert.StartsWith(dest, path, StringComparison.Ordinal);
            Assert.InRange(path.Length - dest.Length, 1, 16);
            Assert.Equal(Path.GetDirectoryName(dest), Path.GetDirectoryName(path));
        }
    }

    // ── verify-failure data-loss proofs ───────────────────────────────────────
    //
    // When the destination copy is corrupted (a flipped byte) or torn (truncated) before verify, the
    // verify FAILS, the SOURCE survives with its original bytes, and the suspect destination and
    // in-flight copy are gone — an interrupted/corrupted transfer never loses the original. The bit-flip
    // case proves the content-hash half of verify; the truncation case proves the size half. Both run
    // entirely in a TempDir — no second physical drive (a real two-drive run is a manual cross-platform
    // check, deliberately NOT faked here).

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
        Assert.Equal(MoveOutcome.VerifyFailed, result.Outcome);
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
        Assert.Equal(MoveOutcome.VerifyFailed, result.Outcome);
        Assert.True(File.Exists(src), "source MUST survive a truncated-dest verify failure");
        Assert.Equal(original, File.ReadAllText(src));
        Assert.False(File.Exists(dest), "the suspect (short) destination must be deleted");
        Assert.NotNull(inFlight);
        Assert.False(File.Exists(inFlight), "no in-flight copy left behind");
    }

    // ── temp-file ownership ───────────────────────────────────────────────────
    //
    // The regression for the silent data loss this design removed: the mover used to derive one fixed,
    // guessable in-flight path from the destination and delete it before every copy, so a user's own
    // file sitting at that name was destroyed without a word.
    //
    // PROPERTY PINNED: a file already present in the destination directory is untouched by a
    // cross-volume move, whether the move succeeds or its verify fails. The mover deletes only paths it
    // minted inside that same call. Be honest about what a random name lets a test assert: because the
    // in-flight name is minted per call from a cryptographic random source, no fixed path can be
    // pre-planted to collide with the next one — so these cases cannot demonstrate a near-miss and do
    // not pretend to. The planted files are named in the SHAPE a minted in-flight file takes, so they
    // would have failed loudly against the fixed-suffix code this replaced.

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

    /// <summary>
    /// A post-copy seam that only records the path production minted, leaving the copy untouched — the
    /// mover's real behaviour, plus the observation the test needs.
    /// </summary>
    private static Func<string, CancellationToken, Task> Recorder(List<string> minted) =>
        (inFlight, _) =>
        {
            minted.Add(inFlight);
            return Task.CompletedTask;
        };

    private static void AssertMintedPathsGone(List<string> minted, string because)
    {
        Assert.NotEmpty(minted); // the seam must actually have fired, or the assertion below is vacuous
        foreach (var path in minted)
        {
            Assert.False(File.Exists(path), because);
        }
    }
}
