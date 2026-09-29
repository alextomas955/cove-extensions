using Cove.Core.Auth;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Options;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Jobs;

public sealed class AuthorizedPagingTests
{
    // Seeds count single-file videos in one folder, titled "Film i" over "raw i.mkv", and returns
    // their entity ids in the order the walk pages them.
    private static async Task<IReadOnlyList<int>> SeedVideosAsync(DbContext db, string dirRoot, int count)
    {
        string folderPath = dirRoot.Replace('\\', '/');
        var ids = await ExecutorTestSeed.SeedVideosAsync(db, count, i => (folderPath, $"raw {i}.mkv", $"Film {i}"));
        for (int i = 0; i < count; i++)
        {
            File.WriteAllText(Path.Combine(dirRoot, $"raw {i}.mkv"), $"bytes-{i}");
        }

        return ids;
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
            IReadOnlyList<int> ids;
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
            var (ext, _) = await ExtensionHarness.CreateWithScopedContextsAsync(
                shared, options, authorization: authz);

            var caller = FakePrincipalAccessor.WithPermissions(Permissions.VideosWrite).Current;
            global::Renamer.AllowedIds allowedIds = (kind, pageIds, token) =>
                global::Renamer.EntityAccessGuard.AllowedOnlyAsync(
                    authz, caller, kind, Permissions.VideosWrite, pageIds, token);

            await ext.RunRenamerKindAsync(
                new global::Renamer.RenameRun(
                    RenamerFileKind.Video, count, "op", options, ChunkEntities: chunkEntities),
                allowedIds, new FakeJobProgress(), default);

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
