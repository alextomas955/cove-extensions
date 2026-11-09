using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

public sealed class StudioDepthLockstepTests
{
    [Theory]
    [InlineData(RenamerFileKind.Video)]
    [InlineData(RenamerFileKind.Image)]
    [InlineData(RenamerFileKind.Audio)]
    [InlineData(RenamerFileKind.Text)]
    public async Task MaxDepthChain_LoadsExactlyMaxParentDepthAncestors(RenamerFileKind kind)
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // direct studio + exactly MaxParentDepth ancestors above it; the walk visits the ancestors
            // only (nearest-first), so a full-depth chain must surface exactly MaxParentDepth entries.
            var ancestors = await SeedAncestorChainAsync(db, CoveRenamerDataPort.MaxParentDepth);
            var direct = new Studio { Name = "direct", ParentId = ancestors[^1].Id };
            db.Set<Studio>().Add(direct);
            await db.SaveChangesAsync();

            int entityId = await SeedEntityWithStudioAsync(db, kind, direct.Id);

            var port = new CoveRenamerDataPort(db);
            var entity = await port.LoadEntityAsync(kind, entityId);

            Assert.NotNull(entity);
            Assert.NotNull(entity!.ParentStudios);
            Assert.Equal(CoveRenamerDataPort.MaxParentDepth, entity.ParentStudios!.Count);

            // Nearest-first: index 0 is the direct studio's immediate parent (the deepest-seeded
            // ancestor), walking toward the root.
            var nearestFirst = Enumerable.Reverse(ancestors).ToList();
            for (int i = 0; i < CoveRenamerDataPort.MaxParentDepth; i++)
            {
                Assert.Equal(nearestFirst[i].Id, entity.ParentStudios[i].Id);
            }
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task OverDepthChain_LeavesTheDeepestAncestorUnmatched()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // One level deeper than supported. The (MaxParentDepth+1)-th ancestor is beyond the hard
            // product depth limit: it is neither eager-loaded nor walked, so it is absent from the
            // surfaced chain. That absence is the explicit contract - a studio nested deeper than the
            // limit simply gets no routing rule (unmatched / no-rule), not a silent mis-hydration.
            var ancestors = await SeedAncestorChainAsync(db, CoveRenamerDataPort.MaxParentDepth + 1);
            var direct = new Studio { Name = "direct", ParentId = ancestors[^1].Id };
            db.Set<Studio>().Add(direct);
            await db.SaveChangesAsync();

            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath: "media/incoming", basename: "clip.mkv", title: "A Clip");
            var video = await db.Set<Video>().FirstAsync(v => v.Id == videoId);
            video.StudioId = direct.Id;
            await db.SaveChangesAsync();

            var port = new CoveRenamerDataPort(db);
            var entity = await port.LoadEntityAsync(RenamerFileKind.Video, videoId);

            Assert.NotNull(entity);
            Assert.NotNull(entity!.ParentStudios);
            Assert.Equal(CoveRenamerDataPort.MaxParentDepth, entity.ParentStudios!.Count);

            var overDepthAncestorId = ancestors[0].Id;
            Assert.DoesNotContain(entity.ParentStudios, s => s.Id == overDepthAncestorId);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    // Seeds count Studio rows from the root down, each saved before the next references its Id, and
    // returns them in seed order: index 0 is the root, index ^1 the studio nearest the entity's
    // direct studio.
    private static async Task<List<Studio>> SeedAncestorChainAsync(DbContext db, int count)
    {
        var chain = new List<Studio>(count);
        int? parentId = null;
        for (int i = 0; i < count; i++)
        {
            var s = new Studio { Name = $"anc-{i}", ParentId = parentId };
            db.Set<Studio>().Add(s);
            await db.SaveChangesAsync();
            chain.Add(s);
            parentId = s.Id;
        }

        return chain;
    }

    // Seeds one entity of kind under directStudioId. The studio is written after the seed because the
    // per-kind seeders take no studio.
    private static async Task<int> SeedEntityWithStudioAsync(
        DbContext db, RenamerFileKind kind, int directStudioId)
    {
        var (entityId, _) = await ExecutorTestSeed.SeedEntityAsync(
            db, kind, "media/incoming", BasenameFor(kind), "An Item");

        switch (kind)
        {
            case RenamerFileKind.Video:
                (await db.Set<Video>().FirstAsync(x => x.Id == entityId)).StudioId = directStudioId;
                break;
            case RenamerFileKind.Image:
                (await db.Set<Image>().FirstAsync(x => x.Id == entityId)).StudioId = directStudioId;
                break;
            case RenamerFileKind.Audio:
                (await db.Set<Audio>().FirstAsync(x => x.Id == entityId)).StudioId = directStudioId;
                break;
            case RenamerFileKind.Text:
                (await db.Set<TextDocument>().FirstAsync(x => x.Id == entityId)).StudioId = directStudioId;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }

        await db.SaveChangesAsync();
        return entityId;
    }

    // An extension the kind plausibly carries, so the seeded row looks like one of its kind.
    private static string BasenameFor(RenamerFileKind kind) => kind switch
    {
        RenamerFileKind.Video => "clip.mkv",
        RenamerFileKind.Image => "shot.jpg",
        RenamerFileKind.Audio => "track.mp3",
        RenamerFileKind.Text => "notes.pdf",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
