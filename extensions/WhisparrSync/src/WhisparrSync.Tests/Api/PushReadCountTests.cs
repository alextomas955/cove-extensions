using System.Net;
using Cove.Core.Entities;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// Today's outbound cost of acting on ONE scene, pinned to a formula: <b>zero whole-movie-set reads and
/// one per-scene read per push handler</b>, so four narrow reads across the four handlers that resolve a
/// single scene.
/// </summary>
/// <remarks>
/// <para>
/// Scoped to v3. All four handlers refuse before the transport on v2 by construction, so there is no v2
/// figure for this shape and this class does not imply one.
/// </para>
/// <para>
/// The fixture seeds a real SQLite <c>CoveContext</c> for the same reason
/// <see cref="PushEndpointConfigGuardTests"/> does: each handler resolves its scene from a Cove read
/// BEFORE reaching Whisparr, so without a resolvable scene every case would stop at the identity
/// refusal and count zero reads while proving nothing. One handler is shared across all four calls, so
/// the total is the run's own aggregate rather than four separate ones added up by hand.
/// </para>
/// <para>
/// The narrow count is asserted alongside the zero, because a handler that stopped reading Whisparr
/// altogether would satisfy the zero on its own and answer every scene "not added".
/// </para>
/// </remarks>
[Trait("Tier", "L1")]
public sealed class PushReadCountTests
{
    private const string StashUuid = "157c9e0d-5f8e-446a-b1c5-dddf3cb5b2d1";
    private const string StashDbEndpoint = "https://stashdb.org/graphql";

    private static async Task<(Ext Ext, int CoveId, IAsyncDisposable Db, IAsyncDisposable Conn)> NewExtensionWithSceneAsync()
    {
        var store = new FakeStore();
        await store.SetAsync(
            "options",
            "{\"BaseUrl\":\"http://stored.local:6969\",\"ApiKey\":\"STORED-KEY\",\"SelectedVersion\":\"v3\"}");

        var (db, conn) = await CoveContextFactory.CreateSqliteContextAsync();
        db.Set<Video>().Add(new Video
        {
            Title = "Scene A",
            RemoteIds = { new VideoRemoteId { Endpoint = StashDbEndpoint, RemoteId = StashUuid } },
        });
        await db.SaveChangesAsync();
        var coveId = db.Set<Video>().AsNoTracking().Single().Id;

        var ext = new Ext();
        ((IStatefulExtension)ext).SetStore(store);
        var services = new ServiceCollection();
        services.AddSingleton<DbContext>(db);
        await ext.InitializeAsync(services.BuildServiceProvider());
        return (ext, coveId, db, conn);
    }

    [Fact]
    public async Task EachSingleScenePushHandler_ReadsOnlyItsOwnScene()
    {
        var (ext, coveId, db, conn) = await NewExtensionWithSceneAsync();
        try
        {
            // An empty answer serves every one of the four: none of them finds a matching movie, so each
            // stops at its handled not-added outcome having paid for exactly one per-scene read.
            var handler = FakeHttpMessageHandler.Sequence(
                FakeHttpMessageHandler.Respond(HttpStatusCode.OK, "application/json", "[]"));
            var client = new WhisparrClient(new HttpClient(handler));

            var before = 0;
            foreach (var (name, invoke) in Handlers(ext, coveId, client))
            {
                await invoke();
                var after = WhisparrRequestCounter.Classify(handler);
                Assert.Equal(0, after.WholeSetMovieReads);
                Assert.Equal(before + 1, after.NarrowMovieReads);
                before = after.NarrowMovieReads;
            }

            var breakdown = WhisparrRequestCounter.Classify(handler);
            Assert.Equal(0, breakdown.WholeSetMovieReads);
            Assert.Equal(4, breakdown.NarrowMovieReads);
            Assert.Equal(0, breakdown.Writes);
        }
        finally
        {
            await db.DisposeAsync();
            await conn.DisposeAsync();
        }
    }

    private static IEnumerable<(string Name, Func<Task> Invoke)> Handlers(Ext ext, int coveId, WhisparrClient client)
    {
        yield return (nameof(Ext.SceneSearchAsync),
            () => ext.SceneSearchAsync(new SceneSearchRequest(coveId), client, default));
        yield return (nameof(Ext.SceneGrabReleaseAsync),
            () => ext.SceneGrabReleaseAsync(new SceneGrabReleaseRequest(coveId, "a-release-guid", 1), client, default));
        yield return (nameof(Ext.SceneReleasesListAsync),
            () => ext.SceneReleasesListAsync(new SceneReleasesRequest(coveId), client, default));
        yield return (nameof(Ext.SceneSearchUpgradesAsync),
            () => ext.SceneSearchUpgradesAsync(new SceneSearchRequest(coveId), client, default));
    }
}
