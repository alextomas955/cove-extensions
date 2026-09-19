using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Renamer.Options;
using Renamer.Tests.Execution;
using Renamer.Tests.TestSupport;

using OperationJournalBudget = Renamer.Renamer.OperationJournalBudget;

namespace Renamer.Tests.Jobs;

/// <summary>
/// A denied entity narrows a page of the whole-library walk without ending it: the entities after it
/// are still reached, whether the denial empties the page or only shortens it.
/// </summary>
/// <remarks>
/// Driven at a chunk size of two over four entities, which is the smallest fixture where a denial can
/// both shorten a page and empty one. The cases above this tier page a whole kind at once, so neither
/// shape occurs there.
/// </remarks>
[Collection(SubstDriveScope.CollectionName)]
public sealed class AuthorizedPagingTests
{
    private static async Task<global::Renamer.Renamer> BuildAsync(
        SharedCacheSqlite shared, RenamerOptions options, IAuthorizationService authz)
    {
        var services = new ServiceCollection();
        services.AddScoped<DbContext>(_ => shared.NewContext());
        services.AddSingleton<IEventBus>(new CapturingEventBus());
        services.AddSingleton(authz);
        var provider = services.BuildServiceProvider();

        var ext = RenamerFixture.Create();
        var store = new ConcurrentFakeStore();
        await new OptionsStore(store).SaveAsync(options);
        ((IStatefulExtension)ext).SetStore(store);
        await ext.InitializeAsync(provider);
        return ext;
    }

    /// <summary>
    /// Seeds <paramref name="count"/> single-file videos in one folder, titled "Film i" over
    /// "raw i.mkv", and returns their entity ids in the order the walk pages them.
    /// </summary>
    private static async Task<int[]> SeedVideosAsync(DbContext db, string dirRoot, int count)
    {
        string folderPath = dirRoot.Replace('\\', '/');
        var (folderId, firstId, _) = await ExecutorTestSeed.SeedVideoAsync(db, folderPath, "raw 0.mkv", "Film 0");
        File.WriteAllText(Path.Combine(dirRoot, "raw 0.mkv"), "bytes-0");

        var ids = new List<int> { firstId };
        for (int i = 1; i < count; i++)
        {
            var video = new Video { Title = $"Film {i}", Organized = true };
            db.Set<Video>().Add(video);
            await db.SaveChangesAsync();
            await ExecutorTestSeed.SeedAdditionalFileAsync(db, folderId, video.Id, $"raw {i}.mkv");
            File.WriteAllText(Path.Combine(dirRoot, $"raw {i}.mkv"), $"bytes-{i}");
            ids.Add(video.Id);
        }

        return [.. ids];
    }

    private static Task<RecordingAuthorizationService> RunOverFourVideosAsync(
        TempDir dir, params int[] deniedIndexes)
        => RunOverVideosAsync(dir, count: 4, chunkEntities: 2, deniedIndexes);

    private static async Task<RecordingAuthorizationService> RunOverVideosAsync(
        TempDir dir, int count, int chunkEntities, int[] deniedIndexes)
    {
        var shared = await SharedCacheSqlite.CreateAsync();
        try
        {
            int[] ids;
            await using (var seedDb = shared.NewContext())
            {
                ids = await SeedVideosAsync(seedDb, dir.Root, count);
            }

            var authz = new RecordingAuthorizationService();
            foreach (int index in deniedIndexes)
            {
                authz.Denied.Add((EntityKinds.Video, ids[index]));
            }

            var options = new RenamerOptions { FilenameTemplate = "$title" };
            var ext = await BuildAsync(shared, options, authz);

            var caller = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite).Current;
            global::Renamer.AllowedIds allowedIds = (kind, pageIds, token) =>
                global::Renamer.EntityAccessGuard.AllowedOnlyAsync(
                    authz, caller, kind, Permissions.VideosWrite, pageIds, token);

            await ext.RunRenamerKindAsync(
                RenamerFileKind.Video, count, new OperationJournalBudget("op"), options, allowedIds,
                new FakeJobProgress(), default, chunkEntities: chunkEntities);

            return authz;
        }
        finally
        {
            await shared.DisposeAsync();
        }
    }

    [Fact]
    public async Task ADeniedEntityInTheFirstPage_LeavesTheRestOfTheWalkRenamed()
    {
        using var dir = new TempDir();

        await RunOverFourVideosAsync(dir, 0);

        Assert.True(File.Exists(Path.Combine(dir.Root, "raw 0.mkv")), "the denied entity must not move");
        Assert.False(File.Exists(Path.Combine(dir.Root, "Film 0.mkv")));
        for (int i = 1; i < 4; i++)
        {
            Assert.True(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")), $"Film {i}.mkv missing");
        }
    }

    [Fact]
    public async Task AFullyDeniedPage_DoesNotEndTheWalk()
    {
        using var dir = new TempDir();

        await RunOverFourVideosAsync(dir, 0, 1);

        Assert.True(File.Exists(Path.Combine(dir.Root, "raw 0.mkv")));
        Assert.True(File.Exists(Path.Combine(dir.Root, "raw 1.mkv")));
        Assert.True(File.Exists(Path.Combine(dir.Root, "Film 2.mkv")), "Film 2.mkv missing");
        Assert.True(File.Exists(Path.Combine(dir.Root, "Film 3.mkv")), "Film 3.mkv missing");
    }

    /// <summary>
    /// A long denied region costs authorization calls proportional to the entities walked over the
    /// page size, never one per denied entity.
    /// </summary>
    /// <remarks>
    /// Twelve entities at a chunk of four, denied from the first page's last allowed entity to the
    /// one before the last. Drawing a page sized to the chunk's remaining capacity makes every draw
    /// after the first a page of one, which the per-entity <c>Asked</c> list cannot see: the walk asks
    /// about the same twelve entities either way, and only the number of round trips differs.
    /// </remarks>
    [Fact]
    public async Task ALongDeniedRegion_CostsOneCallPerPage_NotOnePerDeniedEntity()
    {
        using var dir = new TempDir();

        var authz = await RunOverVideosAsync(
            dir, count: 12, chunkEntities: 4, [3, 4, 5, 6, 7, 8, 9, 10]);

        Assert.Equal(12, authz.Asked.Count);
        Assert.True(authz.BatchCalls <= 4, $"12 entities over pages of 4 took {authz.BatchCalls} calls");
    }
}
