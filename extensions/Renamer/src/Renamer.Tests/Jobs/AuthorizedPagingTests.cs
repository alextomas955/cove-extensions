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

    [Theory]
    // One denial inside the first page of two.
    [InlineData(new[] { 0 }, new[] { 1, 2, 3 })]
    // The whole first page denied: an empty allowed page must not read as the end of the walk.
    [InlineData(new[] { 0, 1 }, new[] { 2, 3 })]
    public async Task DeniedEntitiesStayAtTheirSource_AndTheWalkRenamesTheRest(
        int[] denied, int[] expectedRenamed)
    {
        using var dir = new TempDir();

        await RunOverFourVideosAsync(dir, denied);

        foreach (int i in denied)
        {
            Assert.True(File.Exists(Path.Combine(dir.Root, $"raw {i}.mkv")), $"the denied entity {i} must not move");
            Assert.False(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")));
        }

        foreach (int i in expectedRenamed)
        {
            Assert.True(File.Exists(Path.Combine(dir.Root, $"Film {i}.mkv")), $"Film {i}.mkv missing");
        }
    }

    [Fact]
    public async Task ALongDeniedRegion_CostsOneCallPerPage_NotOnePerDeniedEntity()
    {
        using var dir = new TempDir();

        var authz = await RunOverVideosAsync(
            dir, count: 12, chunkEntities: 4, [3, 4, 5, 6, 7, 8, 9, 10]);

        Assert.Equal(12, authz.Asked.Count);
        Assert.Equal(3, authz.BatchCalls);
    }
}
