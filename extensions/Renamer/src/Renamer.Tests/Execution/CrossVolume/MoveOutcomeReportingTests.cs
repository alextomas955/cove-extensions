using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.CrossVolume;

/// <summary>
/// A move that did not happen is reported under the status its mover classified it as, not under one
/// status standing for every cause.
/// </summary>
/// <remarks>
/// <see cref="MoveOutcomeClassifier.StatusFor"/> holds the mapping; this asserts the executor asks it.
/// A failed content verify is the cause chosen here because it is the one the movers can be made to
/// produce on demand, through <see cref="CrossVolumeMover"/>'s own post-copy fault seam, rather than by
/// arranging a lock or a permission the host may or may not honour. The expected status is transcribed
/// from the mapping by hand: asking the classifier would agree with the executor however far either
/// drifts from the other.
/// </remarks>
public sealed class MoveOutcomeReportingTests
{
    [Fact]
    public async Task CrossVolumeVerifyFails_ReportsVerifyFailed_NotLocked()
    {
        Assert.SkipUnless(SecondVolume.IsAvailable, SecondVolume.UnavailableReason);

        using var src = new TempDir();
        using var dst = new SecondVolume();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string srcFolder = src.Root.Replace('\\', '/');
            string dstFolder = dst.Root.Replace('\\', '/').TrimEnd('/');
            var (_, _, fileId) =
                await ExecutorTestSeed.SeedVideoAsync(db, srcFolder, "clip.mkv", "My Film");

            string oldFull = srcFolder + "/clip.mkv";
            File.WriteAllText(Path.Combine(src.Root, "clip.mkv"), "the real bytes");

            string newFull = dstFolder + "/My Film.mkv";
            Assert.False(
                VolumeClassifier.SameVolume(oldFull, newFull),
                "precondition: the destination must be on a second filesystem, or the same-volume mover "
                    + "runs and there is no copy to verify");

            // Corrupting the closed in-flight copy makes the post-copy verify disagree with the source,
            // which is what the mover classifies as VerifyFailed.
            var cross = new CrossVolumeMover(
                (inFlight, ct) => File.WriteAllTextAsync(inFlight, "corrupted", ct));
            var executor = new RenamerExecutor(
                new CoveRenamerDataPort(db), new CapturingEventBus(), new FakeRevertJournal(),
                "run-test", new DiskMover(), cross);

            var plan = new RenamerPlan(0, RenamerFileKind.Video,
            [
                new RenamerPlanItem(
                    fileId, oldFull, newFull, RenamerStatus.Move, "My Film.mkv", dstFolder),
            ]);

            var result = await executor.ExecuteAsync(plan, new RenamerOptions(), default);

            var item = Assert.Single(result.Skipped);
            Assert.Equal(RenamerStatus.SkipVerifyFailed, item.Status);
            Assert.Empty(result.Renamed);

            // The file stays where it was and the corrupted copy is not left behind, so the status is the
            // only thing this run got wrong.
            Assert.True(File.Exists(Path.Combine(src.Root, "clip.mkv")));
            Assert.Equal("the real bytes", File.ReadAllText(Path.Combine(src.Root, "clip.mkv")));
            Assert.False(File.Exists(Path.Combine(dst.Root, "My Film.mkv")));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
