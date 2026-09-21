using System.Net.Http.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Scene;

// A success status is not the evidence. The command's identifier is taken off the answer to the
// post and read back off the instance, and only that read licenses the sentence saying the
// instance holds the search.
//
// The command bodies below carry the member set the pinned instance's command resource declares,
// read from that server's source. Which status a just-posted command reports is not measured, so
// confirmation is identifier equality alone.
public sealed class SceneSearchReadBackTests
{
    private const string SceneId = "3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9";

    private const int SceneOnTheInstance = 812;

    private const int CommandOnTheInstance = 9001;

    private const string Search = "search";

    private static string HeldSceneRow(bool monitored)
        => $$"""[{"id":{{SceneOnTheInstance}},"monitored":{{(monitored ? "true" : "false")}}}]""";

    // A scene the instance holds no entry for.
    private const string NoSceneRow = "[]";

    private static string CommandRow(int commandId)
        => $$"""
        {"id":{{commandId}},"name":"MoviesSearch","commandName":"Movies Search","priority":"normal","status":"queued","result":"unknown","trigger":"manual","queued":"2026-01-01T00:00:00Z"}
        """;

    // A success carrying no identifier a caller could ask about afterwards.
    private const string CommandRowWithNoReadableId =
        """{"name":"MoviesSearch","status":"queued"}""";

    [Fact]
    public async Task ASearchOnASceneTheInstanceDoesNotHoldSendsNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, NoSceneRow);

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.WhisparrHasNoEntryForScene, result.Refusal);
        Assert.False(result.SearchIsWithWhisparr);
        Assert.Empty(host.Client.Acting);
    }

    [Fact]
    public async Task ASearchOnASceneTheInstanceIsNotMonitoringSendsNothing()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, HeldSceneRow(monitored: false));

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.WhisparrIsNotMonitoringThisScene, result.Refusal);
        Assert.False(result.SearchIsWithWhisparr);
        Assert.Empty(host.Client.Acting);
    }

    [Fact]
    public async Task ACommandThatReadsBackUnderThePostedIdIsWithWhisparrAndClaimsNoDownload()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, HeldSceneRow(monitored: true));
        Confirming(host, CommandOnTheInstance);

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.None, result.Refusal);
        Assert.True(result.SearchIsWithWhisparr);

        // The whole answer, so a member claiming a file, a release or a queue would have to be
        // added here as well as declared.
        Assert.Equal(new SceneActionResult(SceneRefusalKind.None, true), result);
    }

    [Fact]
    public async Task ThePostedCommandIsWhatIsAskedAboutAndTheSceneIsWhatIsSearchedFor()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, HeldSceneRow(monitored: true));
        Confirming(host, CommandOnTheInstance);

        await host.SceneActionAsync(coveId, Search);

        var searched = Assert.Single(host.Client.Acting);
        Assert.Equal(nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync), searched.Verb);
        Assert.Equal(SceneOnTheInstance, searched.EntityId);

        var asked = Assert.Single(
            host.Client.Notifications,
            call => call.Verb == nameof(IWhisparrClient.ReadCommandAsync));
        Assert.Equal(CommandOnTheInstance, asked.Id);
    }

    // The instance answers a whole command resource under a success, so a status assertion cannot
    // tell this case from the confirmed one.
    [Fact]
    public async Task AReadBackNamingAnotherCommandIsRefusedRatherThanConfirmed()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, HeldSceneRow(monitored: true));
        host.Client
            .Answering(
                nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync),
                MonitorHost.Json(201, CommandRow(CommandOnTheInstance)))
            .Answering(
                nameof(IWhisparrClient.ReadCommandAsync),
                MonitorHost.Json(200, CommandRow(CommandOnTheInstance + 1)));

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.InstanceRefused, result.Refusal);
        Assert.False(result.SearchIsWithWhisparr);
    }

    [Fact]
    public async Task APostNamingNoReadableCommandIdIsRefusedAndNothingIsAskedAbout()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, HeldSceneRow(monitored: true));
        host.Client.Answering(
            nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync),
            MonitorHost.Json(201, CommandRowWithNoReadableId));

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.InstanceRefused, result.Refusal);
        Assert.False(result.SearchIsWithWhisparr);
        Assert.DoesNotContain(nameof(IWhisparrClient.ReadCommandAsync), host.Client.Verbs);
    }

    [Fact]
    public async Task AReadBackThatNeverArrivesIsDidNotReachRatherThanConfirmed()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, HeldSceneRow(monitored: true));
        host.Client.Answering(
            nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync),
            MonitorHost.Json(201, CommandRow(CommandOnTheInstance)));
        host.Client.Unreachable.Add(nameof(IWhisparrClient.ReadCommandAsync));

        var result = await host.SceneActionAsync(coveId, Search);

        Assert.Equal(SceneRefusalKind.DidNotReachWhisparr, result.Refusal);
        Assert.False(result.SearchIsWithWhisparr);
    }

    // The calls are counted rather than read off the result, because a loop that settles after one
    // iteration produces the same result as a single read.
    [Fact]
    public async Task OneCommandReadPerSearchAndNoSecond()
    {
        await using var host = await MonitorHost.CreateAsync();
        var coveId = await SeedSceneAsync(host, HeldSceneRow(monitored: true));
        Confirming(host, CommandOnTheInstance);

        await host.SceneActionAsync(coveId, Search);

        Assert.Equal(
            [
                nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync),
                nameof(IWhisparrClient.ReadCommandAsync),
            ],
            host.Client.Verbs);
        Assert.Single(host.Client.SceneStatuses);
    }

    // The Missing tab's cases sit here rather than beside the card's, so the two surfaces' evidence
    // for one verb is stated in one place and a divergence is visible.
    [Fact]
    public async Task TheMissingTabsSearchConfirmsFromTheSameReadBack()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);
        SceneAnswering(host, HeldSceneRow(monitored: true));
        Confirming(host, CommandOnTheInstance);

        var result = await MissingSearchAsync(host, studioId);

        Assert.Equal(MissingSceneActionRefusal.None, result.Refusal);
        var asked = Assert.Single(
            host.Client.Notifications,
            call => call.Verb == nameof(IWhisparrClient.ReadCommandAsync));
        Assert.Equal(CommandOnTheInstance, asked.Id);
    }

    [Fact]
    public async Task TheMissingTabsSearchIsRefusedWhenThePostNamesNoReadableCommandId()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);
        SceneAnswering(host, HeldSceneRow(monitored: true));
        host.Client.Answering(
            nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync),
            MonitorHost.Json(201, CommandRowWithNoReadableId));

        var result = await MissingSearchAsync(host, studioId);

        Assert.Equal(MissingSceneActionRefusal.InstanceRefused, result.Refusal);
        Assert.DoesNotContain(nameof(IWhisparrClient.ReadCommandAsync), host.Client.Verbs);
    }

    [Fact]
    public async Task TheMissingTabsSearchIsDidNotReachWhenTheReadBackNeverArrives()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);
        SceneAnswering(host, HeldSceneRow(monitored: true));
        host.Client.Answering(
            nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync),
            MonitorHost.Json(201, CommandRow(CommandOnTheInstance)));
        host.Client.Unreachable.Add(nameof(IWhisparrClient.ReadCommandAsync));

        var result = await MissingSearchAsync(host, studioId);

        Assert.Equal(MissingSceneActionRefusal.DidNotReachWhisparr, result.Refusal);
    }

    // Asserted per route rather than once. The two answer in different vocabularies, and a
    // read-back added to one and not the other is the inconsistency this pairing catches.
    [Fact]
    public async Task NeitherSurfaceReportsASearchWhisparrDoesNotHoldAsASuccess()
    {
        await using var host = await MonitorHost.CreateAsync();
        var studioId = await StudioIn(host);
        var coveId = await host.SeedStudioSceneAsync(
            studioId, MonitorHost.StoredEndpoint, SceneId);
        SceneAnswering(host, HeldSceneRow(monitored: true));
        host.Client
            .Answering(
                nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync),
                MonitorHost.Json(201, CommandRow(CommandOnTheInstance)))
            .Answering(
                nameof(IWhisparrClient.ReadCommandAsync),
                MonitorHost.Json(404, ""));

        var onTheSceneTab = await host.SceneActionAsync(coveId, Search);
        var onTheMissingTab = await MissingSearchAsync(host, studioId);

        Assert.Equal(SceneRefusalKind.InstanceRefused, onTheSceneTab.Refusal);
        Assert.False(onTheSceneTab.SearchIsWithWhisparr);
        Assert.Equal(MissingSceneActionRefusal.InstanceRefused, onTheMissingTab.Refusal);
    }

    // A post and a read-back that both name the same command.
    private static void Confirming(MonitorHost host, int commandId)
        => host.Client
            .Answering(
                nameof(IWhisparrSceneSearchGrabbing.SearchSceneAsync),
                MonitorHost.Json(201, CommandRow(commandId)))
            .Answering(
                nameof(IWhisparrClient.ReadCommandAsync),
                MonitorHost.Json(200, CommandRow(commandId)));

    private static void SceneAnswering(MonitorHost host, string row)
        => host.Client.Answering(
            nameof(IWhisparrSceneStatusReading.ReadSceneByRemoteIdAsync),
            MonitorHost.Json(200, row));

    private static Task<int> StudioIn(MonitorHost host)
        => host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

    private static async Task<int> SeedSceneAsync(MonitorHost host, string row)
    {
        SceneAnswering(host, row);
        var studioId = await StudioIn(host);
        return await host.SeedStudioSceneAsync(studioId, MonitorHost.StoredEndpoint, SceneId);
    }

    private static async Task<MissingSceneActionResult> MissingSearchAsync(
        MonitorHost host, int studioId)
    {
        var answered = await host.PostRawAsync(
            "studio", studioId, $"missing/{SceneId}/search", "{}");
        answered.EnsureSuccessStatusCode();
        return (await answered.Content.ReadFromJsonAsync<MissingSceneActionResult>(
            TestContext.Current.CancellationToken))!;
    }
}
