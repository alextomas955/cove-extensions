using System.Text.Json.Nodes;
using WhisparrSync.Library;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// Where this product built a folder for an entity, the folder is what the site is compared against
// and moved to. Two sites under one root are both at that root, so a root comparison reads a site
// still sitting at another entity's folder as correctly placed and moves it nowhere.
public sealed class SiteHeldAtAFolderTests
{
    private const int SiteId = 9;

    private const string Site = "a30bc641-6afe-4c80-9c73-ecb68104a68d";

    private const string AgreedRoot = "/i-downloads-p/videos";

    private const string OwnFolder = AgreedRoot + "/.wsync-v2/" + Site;

    private const string AnotherEntitysFolder = AgreedRoot + "/.wsync-v2/somebody-else";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ASiteAtTheRightRootAndTheWrongFolderIsMovedToItsOwn()
    {
        var moves = new List<(int SiteId, string Root, string? Folder)>();

        var outcome = await PassAsync(AnotherEntitysFolder, moves);

        Assert.Equal(SceneRegistration.Moved, outcome.Registration);
        Assert.Equal([(SiteId, AgreedRoot, OwnFolder)], moves);
    }

    [Fact]
    public async Task ASiteAlreadyAtItsOwnFolderIsSentNothing()
    {
        var moves = new List<(int SiteId, string Root, string? Folder)>();

        var outcome = await PassAsync(OwnFolder, moves);

        Assert.Equal(SceneRegistration.AlreadyHeld, outcome.Registration);
        Assert.Empty(moves);
    }

    // The instance answers its own verbatim spelling, and on Windows that is the other separator
    // and whatever case it holds. Compared literally, every site would be moved again on every run.
    [Fact]
    public async Task TheComparisonSurvivesTheInstancesOwnSpellingOfTheSameFolder()
    {
        var moves = new List<(int SiteId, string Root, string? Folder)>();

        var outcome = await PassAsync(
            OwnFolder.Replace('/', '\\').ToUpperInvariant(), moves);

        Assert.Equal(SceneRegistration.AlreadyHeld, outcome.Registration);
        Assert.Empty(moves);
    }

    private static Task<SyncRegistration> PassAsync(
        string heldAt, List<(int SiteId, string Root, string? Folder)> moves)
        => SiteRegistrationStep.RegisterAsync(
            (_, _) => Task.FromResult<WhisparrResponse?>(
                MonitorHost.Json(200, Row(heldAt))),
            (_, _) => throw new InvalidOperationException("This case must send no add."),
            (siteId, root, folder, _) =>
            {
                moves.Add((siteId, root, folder));
                return Task.FromResult<WhisparrResponse?>(MonitorHost.Json(202, "{}"));
            },
            (_, _) => throw new InvalidOperationException("This case must send no re-read."),
            new EntityPlacement(AgreedRoot, OwnFolder),
            new LibrarySiteIdentity(4, Site),
            TestCt);

    // A file count above zero, so a site read as correctly placed is not sent the catalogue re-read
    // that an unread catalogue gets.
    private static string Row(string heldAt)
        => new JsonObject
        {
            ["id"] = SiteId,
            ["title"] = "Exploited College Girls",
            ["rootFolderPath"] = AgreedRoot,
            ["path"] = heldAt,
            ["statistics"] = new JsonObject { ["episodeFileCount"] = 2 },
        }.ToJsonString();
}
