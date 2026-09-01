using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Collisions;

/// <summary>
/// The absolute-path budget survives the executor's own duplicate-suffix loop (integration, SQLite +
/// real disk). The loop runs after the plan was measured and lengthens the name to free a taken slot,
/// so the pair of cases below is the assertion: one budget refuses the suffixed path and leaves the
/// source where it is, and the same arrangement with the suffix's own length added renames through to
/// the suffixed name — which is what proves the loop really fires here rather than the refusal coming
/// from the collision.
/// </summary>
public sealed class SuffixBudgetTests
{
    private const string SourceBasename = "a.mkv";
    private const string PlannedBasename = "target.mkv";

    /// <summary>The name the loop settles on once the planned one is found taken, at the shipped suffix format.</summary>
    private const string SuffixedBasename = "target (1).mkv";

    /// <summary>What " (1)" costs between the stem and the extension.</summary>
    private const int SuffixCost = 4;

    private sealed record Run(
        RenamerExecutor.RenamerRunResult Result, CapturingEventBus Bus, string DbBasename);

    /// <summary>
    /// Renames the seeded source onto a name already present on disk, under a budget derived from the
    /// PLANNED path's own length, so the arrangement cannot drift with the temp directory's depth.
    /// </summary>
    private static async Task<Run> RenameOntoATakenNameAsync(TempDir dir, int budgetOverPlanned)
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, _, fileA) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, SourceBasename, "Film A");

            // The planned name is on disk but holds no file row, so the DB pre-check passes it and the
            // disk-side check is what makes the loop suffix.
            File.WriteAllText(Path.Combine(dir.Root, SourceBasename), "A-bytes");
            File.WriteAllText(Path.Combine(dir.Root, PlannedBasename), "occupant");

            string plannedFullPath = $"{folderPath}/{PlannedBasename}";
            var plan = new RenamerPlan(10, RenamerFileKind.Video,
            [
                new RenamerPlanItem(fileA, $"{folderPath}/{SourceBasename}", plannedFullPath,
                    RenamerStatus.Renamer, PlannedBasename, folderPath),
            ]);

            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(
                new CoveRenamerDataPort(db), bus, new FakeRevertJournal(), "run-test", new DiskMover());

            var result = await executor.ExecuteAsync(
                plan,
                new RenamerOptions { FullPathMax = plannedFullPath.Length + budgetOverPlanned },
                default);

            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileA);
            return new Run(result, bus, basename);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASuffixThatCrossesTheBudget_IsSkipped_AndTheSourceStaysPut()
    {
        using var dir = new TempDir();

        var run = await RenameOntoATakenNameAsync(dir, budgetOverPlanned: 0);

        var skipped = Assert.Single(run.Result.Skipped);
        Assert.Equal(RenamerStatus.SkipTooLong, skipped.Status);
        Assert.Contains("FullPathMax", skipped.Reason);
        Assert.Empty(run.Result.Renamed);
        Assert.Empty(run.Result.Failed);

        Assert.Equal("A-bytes", File.ReadAllText(Path.Combine(dir.Root, SourceBasename)));
        Assert.False(File.Exists(Path.Combine(dir.Root, SuffixedBasename)),
            "the suffixed path the budget forbids must not be written");
        Assert.Equal(SourceBasename, run.DbBasename);
        Assert.Empty(run.Bus.Published);
    }

    [Fact]
    public async Task TheSameArrangement_WithRoomForTheSuffix_RenamesToTheSuffixedName()
    {
        using var dir = new TempDir();

        var run = await RenameOntoATakenNameAsync(dir, budgetOverPlanned: SuffixCost);

        var renamed = Assert.Single(run.Result.Renamed);
        Assert.Equal($"{dir.Root.Replace('\\', '/')}/{SuffixedBasename}", renamed.NewPath);
        Assert.Empty(run.Result.Skipped);

        Assert.Equal("A-bytes", File.ReadAllText(Path.Combine(dir.Root, SuffixedBasename)));
        Assert.Equal(SuffixedBasename, run.DbBasename);
        Assert.Single(run.Bus.Published);
    }
}
