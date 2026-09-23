using Renamer.Execution;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution.Collisions;

public sealed class SuffixBudgetTests
{
    private const string SourceBasename = "a.mkv";
    private const string PlannedBasename = "target.mkv";

    // The name the loop settles on once the planned one is found taken, at the shipped suffix
    // format.
    private const string SuffixedBasename = "target (1).mkv";

    // What " (1)" costs between the stem and the extension.
    private const int SuffixCost = 4;

    private sealed record Run(
        RenamerExecutor.RenamerRunResult Result, CapturingEventBus Bus, string DbBasename);

    // Renames the seeded source onto a name already present on disk, under a budget derived from
    // the planned path's own length, so the arrangement cannot drift with the temp directory's
    // depth.
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
                    RenamerStatus.Rename, PlannedBasename, folderPath),
            ]);

            var bus = new CapturingEventBus();
            var executor = new RenamerExecutor(
                new CoveRenamerDataPort(db), bus, new FakeRevertJournal(), "run-test");

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
