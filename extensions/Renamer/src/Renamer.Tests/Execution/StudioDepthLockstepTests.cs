using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// The mechanical lockstep guard for the studio-ancestor depth. Every assertion here is keyed on
/// <see cref="CoveRenamerDataPort.MaxParentDepth"/> and driven through the REAL per-kind EF Include
/// chain, so it fails if anyone (a) adds or removes a <c>.ThenInclude(s =&gt; s!.Parent)</c> hop in
/// <c>VideoQuery</c>/<c>ImageQuery</c>/<c>AudioQuery</c>, or (b) changes the constant without matching
/// the chains — closing the "the coupling is enforced only by a comment" concern. Runs against a real
/// SQLite-backed <see cref="CoveContext"/> so the self-referencing Studio parent FK hydrates as
/// production would.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class StudioDepthLockstepTests
{
    [Fact]
    public async Task MaxDepthChain_LoadsExactlyMaxParentDepthAncestors_ForVideo()
        => await AssertMaxDepthChainLoads(SeedKind.Video);

    [Fact]
    public async Task MaxDepthChain_LoadsExactlyMaxParentDepthAncestors_ForImage()
        => await AssertMaxDepthChainLoads(SeedKind.Image);

    [Fact]
    public async Task MaxDepthChain_LoadsExactlyMaxParentDepthAncestors_ForAudio()
        => await AssertMaxDepthChainLoads(SeedKind.Audio);

    private static async Task AssertMaxDepthChainLoads(SeedKind kind)
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

            var (renamerKind, entityId) = await SeedEntityWithStudioAsync(db, kind, direct.Id);

            var port = new CoveRenamerDataPort(db);
            var entity = await port.LoadEntityAsync(renamerKind, entityId);

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
            // One level DEEPER than supported. The (MaxParentDepth+1)-th ancestor is beyond the hard
            // product depth limit: it is neither eager-loaded nor walked, so it is ABSENT from the
            // surfaced chain. That absence IS the explicit contract — a studio nested deeper than the
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

            // The root ancestor (seeded first, deepest-above the limit) is absent from the loaded chain.
            var overDepthAncestorId = ancestors[0].Id;
            Assert.DoesNotContain(entity.ParentStudios, s => s.Id == overDepthAncestorId);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    private enum SeedKind { Video, Image, Audio }

    /// Seeds <paramref name="count"/> Studio rows root→leaf (each saved before the next references its
    /// Id) and returns them in seed order (index 0 = root, index ^1 = the studio nearest the entity's
    /// direct studio).
    private static async Task<List<Studio>> SeedAncestorChainAsync(CoveContext db, int count)
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

    private static async Task<(RenamerFileKind kind, int entityId)> SeedEntityWithStudioAsync(
        CoveContext db, SeedKind kind, int directStudioId)
    {
        switch (kind)
        {
            case SeedKind.Video:
                {
                    var (_, id, _) = await ExecutorTestSeed.SeedVideoAsync(
                        db, folderPath: "media/incoming", basename: "clip.mkv", title: "A Clip");
                    var e = await db.Set<Video>().FirstAsync(x => x.Id == id);
                    e.StudioId = directStudioId;
                    await db.SaveChangesAsync();
                    return (RenamerFileKind.Video, id);
                }
            case SeedKind.Image:
                {
                    var (_, id, _) = await ExecutorTestSeed.SeedImageAsync(
                        db, folderPath: "media/incoming", basename: "shot.jpg", title: "A Shot");
                    var e = await db.Set<Image>().FirstAsync(x => x.Id == id);
                    e.StudioId = directStudioId;
                    await db.SaveChangesAsync();
                    return (RenamerFileKind.Image, id);
                }
            case SeedKind.Audio:
                {
                    var (_, id, _) = await ExecutorTestSeed.SeedAudioAsync(
                        db, folderPath: "media/incoming", basename: "track.mp3", title: "A Track");
                    var e = await db.Set<Audio>().FirstAsync(x => x.Id == id);
                    e.StudioId = directStudioId;
                    await db.SaveChangesAsync();
                    return (RenamerFileKind.Audio, id);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }
}
