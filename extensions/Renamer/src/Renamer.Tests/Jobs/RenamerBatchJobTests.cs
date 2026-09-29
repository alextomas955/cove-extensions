using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Jobs;

public sealed class RenamerBatchJobTests
{
    // Wires the extension's captured seams (_scopeFactory, _eventBus, Store) from a DI provider
    // that registers the base DbContext scoped over the test's shared in-memory SQLite connection,
    // so each CreateAsyncScope() (including the per-worker scopes the parallel batch opens)
    // resolves a distinct context over the same database. A singleton registration would hand every
    // parallel worker the one seeded context - a DbContext is not thread-safe, so concurrent
    // workers on it throw/corrupt. The seed/assert context (db) shares the connection, so rows the
    // workers save are visible to the test's read-backs.
    private static async Task<global::Renamer.Renamer> BuildExtensionAsync(SqliteConnection conn, IEventBus bus)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ =>
        {
            var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(conn).Options;
            return new CoveContext(options, principalAccessor: null);
        });
        services.AddSingleton(bus);
        var provider = services.BuildServiceProvider();

        var ext = RenamerFixture.Create();
        var store = new FakeStore();
        // "$title" keeps the seed videos, which have no height, from gaining a resolution suffix.
        // SameVolumeConcurrency=1 because every DI scope here shares one in-memory SQLite connection,
        // and two DbContexts racing on one connection throw inside EF.
        await new global::Renamer.Options.OptionsStore(store).SaveAsync(
            new global::Renamer.Options.RenamerOptions { FilenameTemplate = "$title", SameVolumeConcurrency = 1 });
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider);
        return ext;
    }

    [Fact]
    public async Task EmptyIds_ReportsFinalOne_PerformsZeroRenames()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "keep me.mkv", "Untouched");
            File.WriteAllText(Path.Combine(dir.Root, "keep me.mkv"), "stay");

            var bus = new CapturingEventBus();
            var ext = await BuildExtensionAsync(conn, bus);
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [], progress, default);

            Assert.True(File.Exists(Path.Combine(dir.Root, "keep me.mkv")));
            Assert.Empty(bus.Published);
            Assert.Equal((1d, "Nothing to rename."), Assert.Single(progress.Reports));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task TwoRowsNamingOneSourceFile_RenameNeither_LeaveTheFileAlone()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, v1, file1) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "a.mkv", "First Film");

            // A second folder row for the same directory, spelled with a trailing separator, carrying
            // its own file row for the same basename.
            var twin = new Cove.Core.Entities.Folder { Path = folderPath + "/", ModTime = DateTime.UtcNow };
            db.Set<Cove.Core.Entities.Folder>().Add(twin);
            await db.SaveChangesAsync();
            var video2 = new Cove.Core.Entities.Video { Title = "Second Film", Organized = true };
            db.Set<Cove.Core.Entities.Video>().Add(video2);
            await db.SaveChangesAsync();
            int file2 = await ExecutorTestSeed.SeedAdditionalFileAsync(db, twin.Id, video2.Id, "a.mkv");

            File.WriteAllText(Path.Combine(dir.Root, "a.mkv"), "bytes");

            var bus = new CapturingEventBus();
            var ext = await BuildExtensionAsync(conn, bus);
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [v1, video2.Id], progress, default);

            // The file is untouched and neither row moved, so nothing renamed the file the other claims.
            Assert.True(File.Exists(Path.Combine(dir.Root, "a.mkv")));
            Assert.Equal("bytes", File.ReadAllText(Path.Combine(dir.Root, "a.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "First Film.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "Second Film.mkv")));

            var (b1, _) = await ExecutorTestSeed.ReadFileAsync(db, file1);
            var (b2, _) = await ExecutorTestSeed.ReadFileAsync(db, file2);
            Assert.Equal("a.mkv", b1);
            Assert.Equal("a.mkv", b2);

            Assert.Empty(bus.Published);
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheSameIdTwice_RenamesTheFileOnce()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            var (_, videoId, fileId) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw.mkv", "First Film");
            File.WriteAllText(Path.Combine(dir.Root, "raw.mkv"), "bytes");

            var bus = new CapturingEventBus();
            var ext = await BuildExtensionAsync(conn, bus);

            var progress = new FakeJobProgress();
            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [videoId, videoId], progress, default);

            Assert.Equal(1d, progress.LastPercent);
            Assert.True(File.Exists(Path.Combine(dir.Root, "First Film.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw.mkv")));
            var (basename, _) = await ExecutorTestSeed.ReadFileAsync(db, fileId);
            Assert.Equal("First Film.mkv", basename);
            Assert.Single(bus.Published);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
