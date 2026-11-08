using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Renamer.Execution;
using Renamer.Tests.TestSupport;

namespace Renamer.Tests.Execution;

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

            // Route-on-id: the rule key is the stable id, not the (drift-prone) name.
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
    public async Task LoadEntity_Surfaces_TagIdsPairedWithNames_InJoinOrder()
    {
        // The whole tag-routing cascade keys on TagRefs. If this projection were empty or misordered,
        // every tag rule would silently stop matching with no error anywhere - so prove it against a
        // real entity graph rather than against the fake port the resolver tests use.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath: "media/incoming", basename: "clip.mkv", title: "A Clip");

            var first = new Tag { Name = "anime" };
            var second = new Tag { Name = "raw" };
            db.Set<Tag>().AddRange(first, second);
            await db.SaveChangesAsync();

            db.Set<VideoTag>().AddRange(
                new VideoTag { VideoId = videoId, TagId = first.Id },
                new VideoTag { VideoId = videoId, TagId = second.Id });
            await db.SaveChangesAsync();

            var port = new CoveRenamerDataPort(db);
            var entity = await port.LoadEntityAsync(RenamerFileKind.Video, videoId);

            Assert.NotNull(entity);
            Assert.Equal(
                [(first.Id, first.Name), (second.Id, second.Name)],
                entity!.TagRefs);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadEntity_ABlankTagName_ArrivesAsTheCanonicalMarker_AndIsCarried()
    {
        // A blank tag name is not a state the port can meet: the host normalizes it to
        // TagNameRules.EmptyCanonicalName inside the save and stores that, so what reaches a read is the
        // marker, never an empty string. The marker is carried rather than filtered because Cove's own
        // frontend has no special case for it and renders it as an ordinary tag name - dropping the pair
        // here would make Renamer route on a tag set the user does not see in the host UI.
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath: "media/incoming", basename: "clip.mkv", title: "A Clip");

            var blank = new Tag { Name = "" };
            var named = new Tag { Name = "raw" };
            db.Set<Tag>().AddRange(blank, named);
            await db.SaveChangesAsync();

            db.Set<VideoTag>().AddRange(
                new VideoTag { VideoId = videoId, TagId = blank.Id },
                new VideoTag { VideoId = videoId, TagId = named.Id });
            await db.SaveChangesAsync();

            var port = new CoveRenamerDataPort(db);
            var entity = await port.LoadEntityAsync(RenamerFileKind.Video, videoId);

            Assert.NotNull(entity);
            Assert.Equal(
                [(blank.Id, TagNameRules.EmptyCanonicalName), (named.Id, named.Name)],
                entity!.TagRefs);
            Assert.Equal([TagNameRules.EmptyCanonicalName, "raw"], entity.Tags);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadEntities_MapsEachEntityWithItsOwnFileAndStudioChain()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Three videos, each with a studio chain + a sized file, so the batch path exercises the
            // full Include graph (studio parents, files, performers, tags) the single path exercises.
            var grandparent = new Studio { Name = "Grand Network" };
            db.Set<Studio>().Add(grandparent);
            await db.SaveChangesAsync();
            var parent = new Studio { Name = "Parent Label", ParentId = grandparent.Id };
            db.Set<Studio>().Add(parent);
            await db.SaveChangesAsync();
            var direct = new Studio { Name = "Direct Studio", ParentId = parent.Id };
            db.Set<Studio>().Add(direct);
            await db.SaveChangesAsync();

            var ids = new List<int>();
            for (int n = 1; n <= 3; n++)
            {
                var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                    db, folderPath: $"media/incoming/{n}", basename: $"clip{n}.mkv", title: $"Clip {n}");
                var video = await db.Set<Video>().FirstAsync(v => v.Id == videoId);
                video.StudioId = direct.Id;
                var file = await db.Set<VideoFile>().FirstAsync(f => f.VideoId == videoId);
                file.Size = 1000 + n;
                await db.SaveChangesAsync();
                ids.Add(videoId);
            }

            var port = new CoveRenamerDataPort(db);

            var batch = await port.LoadEntitiesAsync(RenamerFileKind.Video, ids);
            var byId = batch.ToDictionary(e => e.EntityId);
            Assert.Equal(3, batch.Count);

            // Compared against the seed, not against LoadEntityAsync: the two reads share the per-kind
            // query and its mapper, so a differential agrees with itself however either one maps. What
            // only the batch path can get wrong is associating a collection with the wrong root, which
            // is why each entity's own file and studio chain are named here.
            for (int n = 1; n <= 3; n++)
            {
                Assert.True(byId.TryGetValue(ids[n - 1], out var batched));
                Assert.Equal($"Clip {n}", batched.Title);
                Assert.Equal(direct.Id, batched.StudioId);
                Assert.Equal(
                    [(parent.Id, "Parent Label"), (grandparent.Id, "Grand Network")],
                    batched.ParentStudios ?? []);

                var file = Assert.Single(batched.Files);
                Assert.Equal($"clip{n}.mkv", file.Basename);
                Assert.Equal($"media/incoming/{n}", file.ParentFolderPath);
                Assert.Equal(1000 + n, file.SizeBytes);
            }
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadEntities_AKindWithNoQuery_ReadsNothing_EvenForAnIdThatExists()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // The id names a real row, so a kind wrongly routed to the video query would return it.
            // Against an empty database the absence is unfalsifiable.
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath: "media/loose", basename: "clip.mkv", title: "A Film");

            var port = new CoveRenamerDataPort(db);

            Assert.NotEmpty(await port.LoadEntitiesAsync(RenamerFileKind.Video, [videoId]));
            Assert.Empty(await port.LoadEntitiesAsync(RenamerFileKind.Gallery, [videoId]));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadEntities_MissingId_IsOmitted_NoNullSlot_NoThrow()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var (_, videoId, _) = await ExecutorTestSeed.SeedVideoAsync(
                db, folderPath: "media/incoming", basename: "clip.mkv", title: "A Clip");

            var port = new CoveRenamerDataPort(db);
            var result = await port.LoadEntitiesAsync(RenamerFileKind.Video, [videoId, videoId + 9999]);

            Assert.Equal(videoId, Assert.Single(result).EntityId);
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
            Assert.Empty(entity.ParentStudios!);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
