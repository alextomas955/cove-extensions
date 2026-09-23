using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Options;
using Renamer.Planner;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Jobs;

public sealed class RenameChunkPlanningLoadTests
{
    private static async Task<(SqliteConnection Connection, DbContext Db, CommandCountingInterceptor Interceptor,
        ServiceProvider Provider)> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new CommandCountingInterceptor();

        DbContext NewContext() => new CoveContext(
            new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptor)
                .Options,
            principalAccessor: null);

        var db = NewContext();
        await db.Database.EnsureCreatedAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => NewContext());
        services.AddSingleton<IEventBus>(new CapturingEventBus());
        return (connection, db, interceptor, services.BuildServiceProvider());
    }

    private static async Task<global::Renamer.Renamer> BuildAsync(
        ServiceProvider provider, RenamerOptions options)
    {
        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(options);
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider);
        return ext;
    }

    // Plans entities unorganized entities under the only-organized gate and returns how many reader
    // commands the run issued. The gate makes every file plan as a skip, so the chunk acts on
    // nothing and the reads counted are the planning pass's own. The count is taken after the
    // extension is built, so it excludes setup.
    private static async Task<int> PlanningReadsAsync(int entities)
    {
        using var dir = new TempDir();
        var (connection, db, interceptor, provider) = await OpenAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var ids = await ExecutorTestSeed.SeedVideosAsync(
                db, entities, i => (folderPath, $"raw {i}.mkv", $"Film {i}"), organized: false);

            var ext = await BuildAsync(
                provider, new RenamerOptions { FilenameTemplate = "$title", OnlyOrganized = true });
            var progress = new FakeJobProgress();

            interceptor.ReaderCount = 0;
            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, ids, progress, default);

            Assert.Equal(1d, progress.LastPercent);
            return interceptor.ReaderCount;
        }
        finally
        {
            await provider.DisposeAsync();
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task PlanningPass_CostsTheSameReads_ForTenTimesAsManyEntitiesInOneLoadChunk()
    {
        // Both populations sit inside a single IRenamerDataPort.LoadChunkSize, so a bulk load plans
        // either in the same number of round-trips. Ten times the entities for the same reads is the
        // property under test - a read per id would make the larger run cost ten times the smaller.
        const int few = 12;
        const int many = 120;
        Assert.True(many <= IRenamerDataPort.LoadChunkSize, "both populations must fit one load chunk");

        int readsForFew = await PlanningReadsAsync(few);
        int readsForMany = await PlanningReadsAsync(many);

        Assert.True(readsForFew > 0, "the planning pass read nothing at all");
        Assert.Equal(readsForFew, readsForMany);
        Assert.True(readsForMany < few,
            $"a bulk load must cost fewer reads than the smaller population has ids; got {readsForMany}");
    }

    [Fact]
    public async Task PlanningPass_FollowsTheGivenIdOrder_AndPassesOverAnIdThatIsNotThere()
    {
        using var dir = new TempDir();
        var (connection, db, _, provider) = await OpenAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (folderId, firstId, firstFileId) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath, "raw one.mkv", "First Film");
            var second = new Video { Title = "Second Film", Organized = true };
            db.Set<Video>().Add(second);
            await db.SaveChangesAsync();
            var secondFileId = await ExecutorTestSeed.SeedAdditionalFileAsync(
                db, folderId, second.Id, "raw two.mkv");
            File.WriteAllText(Path.Combine(dir.Root, "raw one.mkv"), "bytes-1");
            File.WriteAllText(Path.Combine(dir.Root, "raw two.mkv"), "bytes-2");

            var ext = await BuildAsync(
                provider,
                new RenamerOptions { FilenameTemplate = "$title", SameVolumeConcurrency = 1 });
            var progress = new FakeJobProgress();

            // The absent id sits between the two real ones: the walk must pass over it and still plan
            // the id after it.
            int absent = second.Id + 500;
            await ext.RunRenamerBatchAsync(
                RenamerFileKind.Video, [firstId, absent, second.Id], progress, default);

            Assert.True(File.Exists(Path.Combine(dir.Root, "First Film.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "Second Film.mkv")));
            var (firstBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, firstFileId);
            var (secondBasename, _) = await ExecutorTestSeed.ReadFileAsync(db, secondFileId);
            Assert.Equal("First Film.mkv", firstBasename);
            Assert.Equal("Second Film.mkv", secondBasename);

            // Every id ticks the planning bar, the absent one included, so the bar matches the count
            // the caller asked for.
            var planning = progress.Reports
                .Where(r => r.Message is not null && r.Message.StartsWith("Planning ", StringComparison.Ordinal))
                .Select(r => r.Message)
                .ToList();
            Assert.Equal(["Planning 1/3...", "Planning 2/3...", "Planning 3/3..."], planning);
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await provider.DisposeAsync();
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
