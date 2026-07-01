using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// <c>CoveRenamerDataPort.LoadAllEntityIdsAsync</c>: an <c>AsNoTracking</c> id-only bulk query
/// per <see cref="RenamerFileKind"/>, the enumeration step a whole-library scan needs before
/// running the existing per-id planner over each candidate. Exercised against a real SQLite
/// <c>CoveContext</c> so the EF query shape (and zero-tracking contract) is proven, not faked.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class LoadAllEntityIdsAsyncTests
{
    [Fact]
    public async Task LoadAllEntityIdsAsync_Video_ReturnsBothSeededVideoIds()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, video1Id, _) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films/one", "one.mkv", "One");
            var (_, video2Id, _) = await ExecutorTestSeed.SeedVideoAsync(db, "/library/films/two", "two.mkv", "Two");

            var port = new CoveRenamerDataPort(db);
            var ids = await port.LoadAllEntityIdsAsync(RenamerFileKind.Video, default);

            Assert.Equal(new[] { video1Id, video2Id }.OrderBy(x => x), ids.OrderBy(x => x));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadAllEntityIdsAsync_Image_ReturnsOnlyImageIds_NotVideoOrAudio()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await ExecutorTestSeed.SeedVideoAsync(db, "/library/films", "one.mkv", "One");
            var (_, imageId, _) = await ExecutorTestSeed.SeedImageAsync(db, "/library/pics", "pic.jpg", "Pic");
            await ExecutorTestSeed.SeedAudioAsync(db, "/library/music", "song.mp3", "Song");

            var port = new CoveRenamerDataPort(db);
            var ids = await port.LoadAllEntityIdsAsync(RenamerFileKind.Image, default);

            Assert.Equal([imageId], ids);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadAllEntityIdsAsync_Gallery_ReturnsEmpty_NeverThrows()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await ExecutorTestSeed.SeedVideoAsync(db, "/library/films", "one.mkv", "One");

            var port = new CoveRenamerDataPort(db);
            var ids = await port.LoadAllEntityIdsAsync(RenamerFileKind.Gallery, default);

            Assert.Empty(ids);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadAllEntityIdsAsync_DoesNotTrackAnyEntries()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            await ExecutorTestSeed.SeedVideoAsync(db, "/library/films", "one.mkv", "One");
            await ExecutorTestSeed.SeedImageAsync(db, "/library/pics", "pic.jpg", "Pic");
            await ExecutorTestSeed.SeedAudioAsync(db, "/library/music", "song.mp3", "Song");

            // SeedXAsync leaves its own rows tracked as Unchanged from the seeding SaveChangesAsync
            // calls; clear the tracker first so this assertion isolates LoadAllEntityIdsAsync's own
            // AsNoTracking behavior rather than residue from seeding.
            db.ChangeTracker.Clear();

            var port = new CoveRenamerDataPort(db);
            await port.LoadAllEntityIdsAsync(RenamerFileKind.Video, default);
            await port.LoadAllEntityIdsAsync(RenamerFileKind.Image, default);
            await port.LoadAllEntityIdsAsync(RenamerFileKind.Audio, default);

            Assert.Empty(db.ChangeTracker.Entries());
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
