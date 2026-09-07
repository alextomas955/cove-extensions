using Cove.Core.Entities;
using WhisparrSync.Contracts;
using WhisparrSync.Library;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// Proves the entity-scene enumeration over an in-memory CoveContext: a studio filter
/// (<c>Video.StudioId</c>) and a performer filter (a <c>VideoPerformers</c> membership) each return ONLY the
/// attributed scenes, with their StashDB ids mapped exactly as <c>StreamAllVideosAsync</c> maps them (same
/// endpoint filter, so ThePornDB ids are excluded). This is the local diff source for "add all missing" — the
/// port reads Cove's own library, never StashDB.
/// </summary>
[Trait("Tier", "L1")]
public sealed class EntityLibraryPortTests
{
    private const string StashEndpoint = "https://stashdb.org/graphql";
    private const string TpdbEndpoint = "https://theporndb.net/graphql";

    [Fact]
    public async Task LoadVideosForEntity_Studio_ReturnsOnlyThatStudiosScenes_WithMappedStashIds()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var studio = new Studio { Name = "IEnergy" };
            var otherStudio = new Studio { Name = "Other Studio" };

            db.Set<Video>().Add(new Video
            {
                Title = "Attributed A",
                Studio = studio,
                RemoteIds =
                {
                    new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-a" },
                    new VideoRemoteId { Endpoint = "https://theporndb.net/graphql", RemoteId = "tpdb-1" },
                },
            });
            db.Set<Video>().Add(new Video
            {
                Title = "Attributed B",
                Studio = studio,
                RemoteIds = { new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-b" } },
            });
            db.Set<Video>().Add(new Video
            {
                Title = "Not this studio",
                Studio = otherStudio,
                RemoteIds = { new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-other" } },
            });
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var scenes = await port.LoadVideosForEntityAsync(EntityKind.Studio, studio.Id);

            Assert.Equal(2, scenes.Count);
            Assert.Equal(["Attributed A", "Attributed B"], scenes.Select(s => s.Title).OrderBy(t => t));
            // Same endpoint filter as StreamAllVideosAsync: the ThePornDB id is excluded.
            Assert.Equal(["uuid-a"], scenes.Single(s => s.Title == "Attributed A").StashIds);
            Assert.Equal(["uuid-b"], scenes.Single(s => s.Title == "Attributed B").StashIds);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadVideosForEntity_Performer_ReturnsOnlyThatPerformersScenes_WithMappedStashIds()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var performer = new Performer { Name = "Miyu Aizawa" };
            var otherPerformer = new Performer { Name = "Someone Else" };

            db.Set<Video>().Add(new Video
            {
                Title = "Has the performer",
                VideoPerformers = { new VideoPerformer { Performer = performer } },
                RemoteIds =
                {
                    new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-p" },
                    new VideoRemoteId { Endpoint = "https://theporndb.net/graphql", RemoteId = "tpdb-2" },
                },
            });
            db.Set<Video>().Add(new Video
            {
                Title = "Different performer",
                VideoPerformers = { new VideoPerformer { Performer = otherPerformer } },
                RemoteIds = { new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-x" } },
            });
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var scenes = await port.LoadVideosForEntityAsync(EntityKind.Performer, performer.Id);

            var scene = Assert.Single(scenes);
            Assert.Equal("Has the performer", scene.Title);
            Assert.Equal(["uuid-p"], scene.StashIds); // ThePornDB id excluded, same as StreamAllVideosAsync
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadEntityIdentity_Tag_ResolvesStashIds_ExcludingOtherEndpoints()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var tag = new Tag { Name = "Tattoos" };
            tag.RemoteIds.Add(new TagRemoteId { Endpoint = StashEndpoint, RemoteId = "tag-uuid" });
            tag.RemoteIds.Add(new TagRemoteId { Endpoint = TpdbEndpoint, RemoteId = "tag-tpdb" });
            db.Set<Tag>().Add(tag);
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var identity = await port.LoadEntityIdentityAsync(EntityKind.Tag, tag.Id);

            Assert.NotNull(identity);
            // Endpoint-split exactly as the studio/performer identity: the StashDB id in its own leg, the TPDB id in its.
            Assert.Equal(["tag-uuid"], identity!.StashIds);
            Assert.Equal(["tag-tpdb"], identity.TpdbIds);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    /// <summary>
    /// The tag owned set is not tag-scoped — a scene Cove owns under ANY tag is never "missing" — so answering
    /// a tag here would mean materializing the whole library. The seam refuses instead, which is what stops a
    /// caller reintroducing the unbounded read; the bounded form of the same subtraction is
    /// <c>LoadOwnedRemoteIdsAsync</c> over one catalogue page's candidate ids.
    /// </summary>
    [Fact]
    public async Task LoadVideosForEntity_Tag_IsRefused_RatherThanReadingTheWholeLibrary()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var tag = new Tag { Name = "Tattoos" };

            // Seeded so a regression that resumed the whole-library read would have rows to return and would
            // therefore fail on the refusal rather than pass on an empty database.
            db.Set<Video>().Add(new Video
            {
                Title = "Tagged",
                VideoTags = { new VideoTag { Tag = tag } },
                RemoteIds = { new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-tagged" } },
            });
            db.Set<Video>().Add(new Video
            {
                Title = "Untagged A",
                RemoteIds = { new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-a" } },
            });
            db.Set<Video>().Add(new Video
            {
                Title = "Untagged B",
                RemoteIds = { new VideoRemoteId { Endpoint = StashEndpoint, RemoteId = "uuid-b" } },
            });
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);

            var refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => port.LoadVideosForEntityAsync(EntityKind.Tag, tag.Id));
            Assert.Equal("kind", refused.ParamName);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadAllEntityRefs_Studio_ProjectsCoveIdAndEndpointSplitIds_KeepingIdlessStudios()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var withBoth = new Studio { Name = "IEnergy" };
            var idless = new Studio { Name = "No remote ids" };
            db.Set<Studio>().Add(withBoth);
            db.Set<Studio>().Add(idless);
            withBoth.RemoteIds.Add(new StudioRemoteId { Endpoint = StashEndpoint, RemoteId = "studio-uuid" });
            withBoth.RemoteIds.Add(new StudioRemoteId { Endpoint = TpdbEndpoint, RemoteId = "studio-tpdb" });
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var refs = await port.LoadAllEntityRefsAsync(EntityKind.Studio);

            Assert.Equal(2, refs.Count); // the id-less studio is counted, never dropped
            var mapped = refs.Single(r => r.CoveId == withBoth.Id);
            Assert.Equal(["studio-uuid"], mapped.StashIds); // endpoint-split: StashDB id only
            Assert.Equal(["studio-tpdb"], mapped.TpdbIds); // endpoint-split: TPDB id only
            var empty = refs.Single(r => r.CoveId == idless.Id);
            Assert.Empty(empty.StashIds);
            Assert.Empty(empty.TpdbIds);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadPerformerImages_MatchesByStashIdAndName_ExcludingImageLessPerformers()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // Image-bearing, matched by its StashDB id.
            var byId = new Performer { Name = "Lilly Bell", ImageBlobId = "blob-1" };
            byId.RemoteIds.Add(new PerformerRemoteId { Endpoint = StashEndpoint, RemoteId = "perf-uuid" });
            byId.RemoteIds.Add(new PerformerRemoteId { Endpoint = TpdbEndpoint, RemoteId = "perf-tpdb" });
            // Image-bearing (override only), matched by name in a different case.
            var byName = new Performer { Name = "Jimmy Bud", ImageOverrideBlobId = "blob-2" };
            // Named/id-matched but NO image — must be excluded so the chip falls back to its glyph.
            var imageLess = new Performer { Name = "No Image" };
            imageLess.RemoteIds.Add(new PerformerRemoteId { Endpoint = StashEndpoint, RemoteId = "perf-noimg" });
            db.Set<Performer>().AddRange(byId, byName, imageLess);
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var resolved = await port.LoadPerformerImagesAsync(
                ["perf-uuid", "perf-noimg"], ["jimmy bud"]);

            Assert.Equal(2, resolved.Count); // the id match + the case-insensitive name match; the image-less one dropped
            var idMatch = resolved.Single(p => p.CoveId == byId.Id);
            Assert.Equal(["perf-uuid"], idMatch.StashIds); // endpoint-split: the TPDB id is excluded
            Assert.Contains(resolved, p => p.CoveId == byName.Id);
            Assert.DoesNotContain(resolved, p => p.CoveId == imageLess.Id);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadAllEntityRefs_Performer_ProjectsCoveIdAndEndpointSplitIds()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            var performer = new Performer { Name = "Miyu Aizawa" };
            db.Set<Performer>().Add(performer);
            performer.RemoteIds.Add(new PerformerRemoteId { Endpoint = StashEndpoint, RemoteId = "perf-uuid" });
            performer.RemoteIds.Add(new PerformerRemoteId { Endpoint = TpdbEndpoint, RemoteId = "perf-tpdb" });
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var refs = await port.LoadAllEntityRefsAsync(EntityKind.Performer);

            var mapped = Assert.Single(refs);
            Assert.Equal(performer.Id, mapped.CoveId);
            Assert.Equal(["perf-uuid"], mapped.StashIds);
            Assert.Equal(["perf-tpdb"], mapped.TpdbIds);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadStudioChildIdentities_Parent_ResolvesEachChildsStashId_EndpointSplitExcludingTpdb()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // A parent studio with two child sub-studios, each carrying its own StashDB + TPDB id. The union read
            // keys on the children's StashDB ids, so the child read must endpoint-split them exactly as the
            // parent's own identity does (the TPDB id stays out of the StashDB leg).
            var parent = new Studio { Name = "Parent Network" };
            var childA = new Studio { Name = "Child A" };
            childA.RemoteIds.Add(new StudioRemoteId { Endpoint = StashEndpoint, RemoteId = "child-a-stash" });
            childA.RemoteIds.Add(new StudioRemoteId { Endpoint = TpdbEndpoint, RemoteId = "child-a-tpdb" });
            var childB = new Studio { Name = "Child B" };
            childB.RemoteIds.Add(new StudioRemoteId { Endpoint = StashEndpoint, RemoteId = "child-b-stash" });
            childB.RemoteIds.Add(new StudioRemoteId { Endpoint = TpdbEndpoint, RemoteId = "child-b-tpdb" });
            parent.Children.Add(childA);
            parent.Children.Add(childB);
            db.Set<Studio>().Add(parent);
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var children = await port.LoadStudioChildIdentitiesAsync(parent.Id);

            Assert.Equal(2, children.Count);
            // Both children's StashDB ids resolve; the endpoint split keeps each TPDB id in its own leg, out of StashIds.
            Assert.Equal(
                ["child-a-stash", "child-b-stash"],
                children.SelectMany(c => c.StashIds).OrderBy(id => id));
            Assert.Equal(
                ["child-a-tpdb", "child-b-tpdb"],
                children.SelectMany(c => c.TpdbIds).OrderBy(id => id));
            // Each child's NAME rides along with its ids. It is the label the sub-studio control renders, and
            // Cove's hierarchy is the only place it exists — no provider aggregate lists a network's children.
            Assert.Equal(["Child A", "Child B"], children.Select(c => c.Name).OrderBy(name => name));
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    [Fact]
    public async Task LoadStudioChildIdentities_ChildlessStudio_IsEmpty_SoTheCatalogueIdSetIsExactlyTheOwnSingleId()
    {
        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        try
        {
            // The non-parent lock: a childless studio must never widen to a union — its catalogue stays the single
            // own-id read it has always been.
            var studio = new Studio { Name = "Standalone" };
            studio.RemoteIds.Add(new StudioRemoteId { Endpoint = StashEndpoint, RemoteId = "own-stash" });
            db.Set<Studio>().Add(studio);
            await db.SaveChangesAsync();

            var port = new CoveLibraryPort(db, StashEndpoint, TpdbEndpoint);
            var children = await port.LoadStudioChildIdentitiesAsync(studio.Id);
            var identity = await port.LoadEntityIdentityAsync(EntityKind.Studio, studio.Id);

            Assert.Empty(children);

            // Mirror the endpoint's id-set composition (own id unioned with each distinct child StashDB id) to
            // prove the childless case collapses to a single id.
            var ownStashId = identity!.StashIds.Single();
            var childStashIds = children.SelectMany(c => c.StashIds).Where(id => !string.IsNullOrEmpty(id));
            IReadOnlyList<string> catalogueIds =
                [ownStashId, .. childStashIds.Where(id => !string.Equals(id, ownStashId, StringComparison.OrdinalIgnoreCase))];

            Assert.Single(catalogueIds);
            Assert.Equal("own-stash", catalogueIds[0]);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
