using Cove.Core.Auth;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Preview;

public sealed class PreviewPlanningLoadTests
{
    // Every entity is unorganized under the only-organized gate, so each plans as a skip and the reads
    // counted are the preview's loading alone.
    private static async Task<int> PreviewReadsAsync(int entities)
    {
        using var dir = new TempDir();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new CommandCountingInterceptor();
        await using var db = new CoveContext(
            new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).AddInterceptors(interceptor).Options,
            principalAccessor: null);
        await db.Database.EnsureCreatedAsync();

        string folderPath = dir.Root.Replace('\\', '/');
        var ids = await ExecutorTestSeed.SeedVideosAsync(
            db, entities, i => (folderPath, $"raw {i}.mkv", $"Film {i}"), organized: false);

        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        await new OptionsStore(store).SaveAsync(new RenamerOptions { OnlyOrganized = true });
        ((Cove.Plugins.IStatefulExtension)ext).SetStore(store);

        interceptor.ReaderCount = 0;
        await ext.PreviewAsync(
            new global::Renamer.Api.RenamerRequest("video", [.. ids]), db,
            FakePrincipalAccessor.WithPermissions(Permissions.VideosRead), default);
        return interceptor.ReaderCount;
    }

    [Fact]
    public async Task Preview_CostsTheSameReads_ForTenTimesAsManyEntitiesInOneLoadChunk()
    {
        const int few = 12;
        const int many = 120;
        Assert.True(many <= CoveRenamerDataPort.LoadChunkSize, "both populations must fit one load chunk");

        int readsForFew = await PreviewReadsAsync(few);
        int readsForMany = await PreviewReadsAsync(many);

        Assert.True(readsForFew > 0, "the preview read nothing at all");
        Assert.Equal(readsForFew, readsForMany);
    }
}
