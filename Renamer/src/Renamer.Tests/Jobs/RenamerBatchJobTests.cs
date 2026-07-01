using Cove.Core.Events;
using Cove.Data;
using Cove.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Jobs;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Jobs;

/// <summary>
/// Batch core: the shared <c>RunRenamerBatchAsync</c> opens a scope via the captured
/// <c>IServiceScopeFactory</c>, builds the port+executor over the real <c>CoveContext</c>,
/// renames every id on disk + in the DB, and reports per-item progress plus a final <c>1.0</c>.
/// Bad/empty input is a clean no-op that still reports the final <c>1.0</c>.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class RenamerBatchJobTests
{
    /// <summary>
    /// Wires the extension's captured seams (<c>_scopeFactory</c>, <c>_eventBus</c>, <c>Store</c>)
    /// from a DI provider that registers the base <c>DbContext</c> SCOPED over the test's shared
    /// in-memory SQLite connection, so each <c>CreateAsyncScope()</c> (including the per-worker scopes
    /// the parallel batch opens) resolves a DISTINCT context over the SAME database. A singleton
    /// registration would hand every parallel worker the one seeded context — a <c>DbContext</c> is
    /// not thread-safe, so concurrent workers on it throw/corrupt. The seed/assert context (<c>db</c>)
    /// shares the connection, so rows the workers save are visible to the test's read-backs.
    /// </summary>
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

        var ext = new global::Renamer.Renamer();
        var store = new FakeStore();
        // These job tests assert batch renamer mechanics over height-less seed videos and expect a
        // stable "$title.ext" output; pin the title-only template so the shipped default (which
        // appends "[$resolution]") doesn't perturb the asserted names.
        await new global::Renamer.Options.OptionsStore(store).SaveAsync(new global::Renamer.Options.RenamerOptions { FilenameTemplate = "$title" });
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider); // captures IServiceScopeFactory + IEventBus from DI
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
            // Two distinct videos sharing ONE folder (a second SeedVideoAsync would re-insert the
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

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", [v1, v2]), progress, default);

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

            // Progress: PHASE B reports per COMPLETED unit (done/total), so a 2-item batch emits a
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

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("video", []), progress, default);

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
    public async Task UnsupportedEntityType_IsCleanNoOp_ReportsFinalOne()
    {
        using var dir = new TempDir();
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var bus = new CapturingEventBus();
            var ext = await BuildExtensionAsync(conn, bus);
            var progress = new FakeJobProgress();

            await ext.RunRenamerBatchAsync(RenamerJob.Encode("gallery", [1, 2]), progress, default);

            Assert.Empty(bus.Published);
            Assert.Equal(1d, progress.LastPercent);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
