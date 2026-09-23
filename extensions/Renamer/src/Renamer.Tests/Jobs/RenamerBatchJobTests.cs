using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Tests.Execution;
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
    public async Task RenamesEveryId_OnDiskAndInDb_ReportsPerItemPlusFinalOne()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            string folderPath = dir.Root.Replace('\\', '/');
            // Two distinct videos sharing one folder (a second SeedVideoAsync would re-insert the
            // folder and trip the folders.Path unique index). Seed the folder+video once, then add
            // a second video + file in the same folder.
            var (folderId, v1, file1) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw one.mkv", "First Film");

            var video2 = new Cove.Core.Entities.Video { Title = "Second Film", Organized = true };
            db.Set<Cove.Core.Entities.Video>().Add(video2);
            await db.SaveChangesAsync();
            var file2 = await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, video2.Id, "raw two.mkv");
            var v2 = video2.Id;

            // Real on-disk sources matching the seeded rows.
            File.WriteAllText(Path.Combine(dir.Root, "raw one.mkv"), "bytes-1");
            File.WriteAllText(Path.Combine(dir.Root, "raw two.mkv"), "bytes-2");

            var bus = new CapturingEventBus();
            var ext = await BuildExtensionAsync(conn, bus);
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerFileKind.Video, [v1, v2], progress, default);

            // Disk: both renamed to "$title.mkv", old gone, content intact.
            Assert.True(File.Exists(Path.Combine(dir.Root, "First Film.mkv")));
            Assert.True(File.Exists(Path.Combine(dir.Root, "Second Film.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw one.mkv")));
            Assert.False(File.Exists(Path.Combine(dir.Root, "raw two.mkv")));
            Assert.Equal("bytes-1", File.ReadAllText(Path.Combine(dir.Root, "First Film.mkv")));

            // DB: basenames updated.
            var (b1, _) = await ExecutorTestSeed.ReadFileAsync(db, file1);
            var (b2, _) = await ExecutorTestSeed.ReadFileAsync(db, file2);
            Assert.Equal("First Film.mkv", b1);
            Assert.Equal("Second Film.mkv", b2);

            // Progress: The execution pass reports per completed unit (done/total), so a 2-item batch emits a
            // sub-1.0 progress tick before the final 1.0. Under parallelism the exact fraction order is
            // nondeterministic; assert that per-item progress is emitted and the run ends at 1.0.
            Assert.Contains(progress.Reports, r => r.Percent is > 0d and < 1d);
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
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

            // Untouched on disk; no renamer event published; only a final 1.0 reported.
            Assert.True(File.Exists(Path.Combine(dir.Root, "keep me.mkv")));
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

            await ext.RunRenamerBatchAsync(
                RenamerFileKind.Video, [videoId, videoId], new FakeJobProgress(), default);

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
