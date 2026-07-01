using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Planner;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

/// <summary>
/// Host-fact proof that <see cref="CoveRenamerDataPort.LoadEntityAsync"/> surfaces the three routing
/// foundations onto the Renamer-owned DTO from a REAL Cove entity graph: the stable studio id, the
/// nearest-first parent-studio chain, and each file's projected byte size. Runs against a SQLite-backed
/// <see cref="Cove.Data.CoveContext"/> (not EF-InMemory) so the self-referencing Studio parent FK and
/// the relational graph hydrate exactly as production would (per MEMORY: bind the base DbContext;
/// SQLite for graph-shape fidelity). Without these fields surfacing, Plan 02's resolver could not route
/// on a stable id and Plan 04's free-space guard would have no per-file bytes to sum.
/// </summary>
[Trait("Tier", "Integration")]
public sealed class CoveDataPortRoutingFieldsTests
{
    [Fact]
    public async Task LoadEntity_Surfaces_StableStudioId_NearestFirstParentChain_AndFileSize()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath: "media/incoming", basename: "clip.mkv", title: "A Clip");

            // A two-level studio chain: grandparent ← parent ← direct. The Video belongs to the direct
            // studio; the parent (index 0 of the walk) is the immediate parent of the direct studio.
            var grandparent = new Studio { Name = "Grand Network" };
            db.Set<Studio>().Add(grandparent);
            await db.SaveChangesAsync();

            var parent = new Studio { Name = "Parent Label", ParentId = grandparent.Id };
            db.Set<Studio>().Add(parent);
            await db.SaveChangesAsync();

            var direct = new Studio { Name = "Direct Studio", ParentId = parent.Id };
            db.Set<Studio>().Add(direct);
            await db.SaveChangesAsync();

            // Attach the direct studio to the seeded video and give the file a known non-zero size.
            var video = await db.Set<Video>().FirstAsync(v => v.Id == videoId);
            video.StudioId = direct.Id;
            var file = await db.Set<VideoFile>().FirstAsync(f => f.VideoId == videoId);
            file.Size = 4096;
            await db.SaveChangesAsync();

            var port = new CoveRenamerDataPort(db);
            var entity = await port.LoadEntityAsync(RenamerFileKind.Video, videoId);

            Assert.NotNull(entity);

            // Route-on-id: the rule key is the STABLE id, not the (drift-prone) name.
            Assert.Equal(direct.Id, entity!.StudioId);

            // Parent chain is nearest-first: index 0 is the direct studio's immediate parent.
            Assert.NotNull(entity.ParentStudios);
            Assert.NotEmpty(entity.ParentStudios!);
            Assert.Equal(parent.Id, entity.ParentStudios![0].Id);
            Assert.Equal(parent.Name, entity.ParentStudios[0].Name);
            // The grandparent follows the parent (bounded eager walk hydrated it).
            Assert.Equal(grandparent.Id, entity.ParentStudios[1].Id);
            Assert.Equal(grandparent.Name, entity.ParentStudios[1].Name);

            // Projected bytes for the free-space sum surface from BaseFileEntity.Size.
            Assert.Equal(4096, entity.Files[0].SizeBytes);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadEntity_Surfaces_ThreeAncestorLevels_MatchingTheWalkDepth()
    {
        // A three-level ancestor chain: great-grandparent ← grandparent ← parent ← direct. The eager
        // include chain loads exactly as many ancestor levels as the walk visits, so a studio rule
        // keyed on the third ancestor up still surfaces rather than silently going unmatched.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath: "media/incoming", basename: "clip.mkv", title: "A Clip");

            var greatGrand = new Studio { Name = "Conglomerate" };
            db.Set<Studio>().Add(greatGrand);
            await db.SaveChangesAsync();

            var grandparent = new Studio { Name = "Grand Network", ParentId = greatGrand.Id };
            db.Set<Studio>().Add(grandparent);
            await db.SaveChangesAsync();

            var parent = new Studio { Name = "Parent Label", ParentId = grandparent.Id };
            db.Set<Studio>().Add(parent);
            await db.SaveChangesAsync();

            var direct = new Studio { Name = "Direct Studio", ParentId = parent.Id };
            db.Set<Studio>().Add(direct);
            await db.SaveChangesAsync();

            var video = await db.Set<Video>().FirstAsync(v => v.Id == videoId);
            video.StudioId = direct.Id;
            await db.SaveChangesAsync();

            var port = new CoveRenamerDataPort(db);
            var entity = await port.LoadEntityAsync(RenamerFileKind.Video, videoId);

            Assert.NotNull(entity);
            Assert.NotNull(entity!.ParentStudios);
            // Nearest-first, three ancestor levels deep, all hydrated.
            Assert.Equal(3, entity.ParentStudios!.Count);
            Assert.Equal(parent.Id, entity.ParentStudios[0].Id);
            Assert.Equal(grandparent.Id, entity.ParentStudios[1].Id);
            Assert.Equal(greatGrand.Id, entity.ParentStudios[2].Id);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadEntity_NoStudio_YieldsNullId_EmptyParentChain_AndDoesNotThrow()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Seed a Video with no studio at all (StudioId left null by SeedVideoAsync).
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath: "media/loose", basename: "lonely.mkv", title: "No Studio");

            var port = new CoveRenamerDataPort(db);

            // classify-not-throw: a missing studio must never raise.
            var entity = await port.LoadEntityAsync(RenamerFileKind.Video, videoId);

            Assert.NotNull(entity);
            Assert.Null(entity!.StudioId);
            Assert.True(entity.ParentStudios is null || entity.ParentStudios.Count == 0);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
