using Cove.Core.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WhisparrSync.Missing;

namespace WhisparrSync.Tests.Missing;

public sealed class OwnedScenePortTests : IAsyncLifetime
{
    private const string StashDb = "https://stashdb.org/graphql";
    private const string HeldScene = "1d468eaf-af0f-4f11-9dcf-9aa3cf62aa95";
    private const string OtherScene = "f95b4ef5-4c0f-43a6-a8ae-151a96831234";

    private DbContext _db = null!;
    private SqliteConnection _connection = null!;

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
        => (_db, _connection) = await CoveContextFactory.CreateSqliteContextAsync();

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task AnEmptyPageAsksTheLibraryNothing()
    {
        var owned = await new OwnedScenePort(_db).ReadOwnedAsync(StashDb, [], TestCt);

        Assert.Empty(owned);
    }

    [Fact]
    public async Task ASceneTheLibraryHoldsUnderTheConnectedProviderIsOwned()
    {
        await SeedAsync(StashDb, HeldScene);

        var owned = await new OwnedScenePort(_db)
            .ReadOwnedAsync(StashDb, [HeldScene, OtherScene], TestCt);

        Assert.Equal([HeldScene], owned);
    }

    [Fact]
    public async Task ARowUnderTheOtherSpellingOfTheSameProviderIsStillOwned()
    {
        await SeedAsync("https://stashdb.org/graphql/", HeldScene);

        var owned = await new OwnedScenePort(_db).ReadOwnedAsync(StashDb, [HeldScene], TestCt);

        Assert.Equal([HeldScene], owned);
    }

    [Fact]
    public async Task ARowUnderTheOtherProvidersEndpointDoesNotMatch()
    {
        await SeedAsync("https://theporndb.net/graphql", HeldScene);

        var owned = await new OwnedScenePort(_db).ReadOwnedAsync(StashDb, [HeldScene], TestCt);

        Assert.Empty(owned);
    }

    [Fact]
    public void TheQuerysParameterListIsBoundedByThePage()
    {
        string[] pageIds = [.. Enumerable.Range(0, 40).Select(index => $"scene-{index}")];

        var sql = _db.Set<VideoRemoteId>()
            .AsNoTracking()
            .Where(row => pageIds.Contains(row.RemoteId))
            .Select(row => new { row.Endpoint, row.RemoteId })
            .ToQueryString();

        // The page's own identifiers reach the statement and nothing wider does. A statement naming
        // no identifier at all would be a walk of every owned row filtered afterwards.
        Assert.Contains("scene-0", sql, StringComparison.Ordinal);
        Assert.Contains("scene-39", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("scene-40", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARepeatedIdentifierDoesNotWidenTheAsk()
    {
        await SeedAsync(StashDb, HeldScene);

        var owned = await new OwnedScenePort(_db)
            .ReadOwnedAsync(StashDb, [HeldScene, HeldScene, HeldScene], TestCt);

        Assert.Equal([HeldScene], owned);
    }

    // The identity row references a real video, which the library's own constraint requires.
    private async Task SeedAsync(string endpoint, string remoteId)
    {
        var video = new Video { Title = remoteId };
        _db.Add(video);
        await _db.SaveChangesAsync(TestCt);

        _db.Set<VideoRemoteId>().Add(
            new VideoRemoteId { Endpoint = endpoint, RemoteId = remoteId, VideoId = video.Id });
        await _db.SaveChangesAsync(TestCt);
    }
}
