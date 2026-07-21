using Cove.Core.Entities;
using WhisparrSync.Library;
using WhisparrSync.Monitor;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Library;

/// <summary>
/// Proves the entity-scene enumeration over an in-memory CoveContext: a studio filter
/// (<c>Video.StudioId</c>) and a performer filter (a <c>VideoPerformers</c> membership) each return ONLY the
/// attributed scenes, with their StashDB ids mapped exactly as <c>LoadAllVideosAsync</c> maps them (same
/// endpoint filter, so ThePornDB ids are excluded). This is the local diff source for "add all missing" — the
/// port reads Cove's own library, never StashDB.
/// </summary>
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
            // Same endpoint filter as LoadAllVideosAsync: the ThePornDB id is excluded.
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
            Assert.Equal(["uuid-p"], scene.StashIds); // ThePornDB id excluded, same as LoadAllVideosAsync
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }
}
