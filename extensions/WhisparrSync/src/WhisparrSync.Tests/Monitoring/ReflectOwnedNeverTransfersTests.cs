using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// Asserted on the bytes that leave, not on the constants they are composed from: the body is
// composed below the seam a call site can see.
public sealed class ReflectOwnedNeverTransfersTests
{
    // Both Whisparr generations offer exactly these two import modes. "copy" links when it can;
    // "move" takes the file out of the library.
    private const string Linking = "copy";

    private const string Moving = "move";

    private const string TheOnlyCommand = "ManualImport";

    private const string LinksIntoPlace = """{"copyUsingHardlinks":true}""";

    private const string CopiesInstead = """{"copyUsingHardlinks":false}""";

    // One folder's parse answer, with everything an attach is composed from.
    private const string Attachable = """
        [{"path":"/library/vixen/2026/scene.mp4","folderName":"2026",
          "quality":{"quality":{"id":7}},"languages":[{"id":1}],"movie":{"id":31}}]
        """;

    // A row the parse could not match omits the quality member rather than sending it null, so
    // exclusion keys on absence.
    private const string Unmatchable = """
        [{"path":"/library/vixen/2026/scene.mp4","folderName":"2026","movie":{"id":31}}]
        """;

    private const string Earlier = "/library/vixen/2025";
    private const string Later = "/library/vixen/2026";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ARunOverTwoFoldersComposesOneLinkingImportPerFolder()
    {
        await using var host = (await RunOverAsync(Attachable)).Host;
        var commands = Commands(host);

        Assert.Equal(2, commands.Count);
        Assert.All(commands, body => Assert.Equal(TheOnlyCommand, body["name"]!.GetValue<string>()));
        Assert.All(commands, body => Assert.Equal(Linking, body["importMode"]!.GetValue<string>()));
    }

    // Asserted over every recorded body, not the command bodies alone: the mode that reaches an
    // instance is not decided by which request this product thinks composed it.
    [Fact]
    public async Task NoOutboundBodyNamesTheModeThatMovesTheFile()
    {
        await using var host = (await RunOverAsync(Attachable)).Host;
        var sent = Bodies(host);

        Assert.NotEmpty(sent);
        Assert.Equal([Linking], ImportModes(host));
        Assert.All(
            sent,
            body => Assert.DoesNotContain(
                $"\"importMode\":\"{Moving}\"", body, StringComparison.OrdinalIgnoreCase));
    }

    // The command names are read out of the bodies and compared against an allow-set of one name,
    // so a rename, organize, delete or search command fails here without being listed.
    [Fact]
    public async Task TheWholePathNamesOneCommandAndIssuesNoDelete()
    {
        await using var host = (await RunOverAsync(Attachable)).Host;

        Assert.DoesNotContain(host.Bytes!.Requests, sent => sent.Method == HttpMethod.Delete);
        Assert.Equal([TheOnlyCommand], CommandNames(host));
        Assert.All(
            Bodies(host),
            body => Assert.All(
                ComposedAdds.GrabbingCommandNames,
                grabbing => Assert.DoesNotContain(grabbing, body, StringComparison.Ordinal)));
    }

    // The refused count matters as much as the linked one: counting an unmatched folder as refused
    // would claim the instance declined something it was never sent.
    [Fact]
    public async Task AFolderWhoseRowsCannotBeMatchedSendsNothingAndIsNotCountedEitherWay()
    {
        var (driven, progress) = await RunOverAsync(Unmatchable);
        await using var host = driven;

        Assert.Empty(Commands(host));
        Assert.Contains(progress.Reports, report => report.SubTask == "0 linked, 0 refused.");
    }

    [Fact]
    public async Task TheHardLinkSettingBeingOffCostsOneReadAndNothingElse()
    {
        var bytes = BodyRecordingHandler.Answering(HttpStatusCode.OK, CopiesInstead);
        await using var host = await MonitorHost.CreateAsync(bytes: bytes);
        var studioId = await SeededStudio(host);
        await host.SeedStudioFileAsync(studioId, Later);

        var answered = await host.ReflectOwnedViewAsync("studio", studioId);

        Assert.Equal(ReflectOwnedSkipReason.HardLinksOff, answered.Skipped);
        var only = Assert.Single(bytes.Requests);
        Assert.Equal(HttpMethod.Get, only.Method);
        Assert.EndsWith("/config/mediamanagement", only.Path, StringComparison.Ordinal);
    }

    // Asserted by reflection over the request contract, so a folder or profile member added later
    // fails here rather than at whichever surface first sends one.
    [Fact]
    public void TheMonitorRequestDeclaresOneOptionalScopeAndNothingElse()
    {
        var declared = typeof(MonitorEntityRequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToList();

        var only = Assert.Single(declared);
        Assert.Equal(nameof(MonitorEntityRequest.Scope), only.Name);
        Assert.Equal(typeof(MonitorScope?), only.PropertyType);
    }

    // Driven over an entity the instance already holds, which is the branch a profile or root-folder
    // read is pointless on and the one a careless add would still reach.
    [Fact]
    public async Task TheAutomaticRunAddsNoReadToTheClickThatStartsIt()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client.Answering(nameof(RecordingWhisparrClient.SetStudioMonitoredAsync), MonitorHost.Json(200, "{}"));
        host.Client
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
                MonitorHost.Json(200, LinksIntoPlace))
            .Answering(
                nameof(IWhisparrStudioActing.ReadStudioAsync),
                MonitorHost.Json(200, """{"id":9,"monitored":false}"""),
                MonitorHost.Json(200, """{"id":9,"monitored":true}"""));
        var studioId = await SeededStudio(host);
        await host.SeedStudioFileAsync(studioId, Later);

        Assert.Equal(MonitorRefusalKind.None, (await host.MonitorAsync(studioId)).Refusal);

        Assert.Single(host.Jobs.Enqueued);
        Assert.All(
            new[]
            {
                nameof(IWhisparrClient.ReadQualityProfilesAsync),
                nameof(IWhisparrClient.ReadRootFoldersAsync),
                nameof(IWhisparrReflectOwnedActing.ListImportableFilesAsync),
                nameof(IWhisparrReflectOwnedActing.AttachOwnedFilesAsync),
            },
            verb => Assert.DoesNotContain(verb, host.Client.Verbs));
    }

    // The answers are keyed on the path each read asks for rather than on call order, so a read
    // added to the run does not hand one call's answer to another.
    private static async Task<(MonitorHost Host, RecordingJobProgress Progress)> RunOverAsync(
        string rows)
    {
        var bytes = BodyRecordingHandler.AnsweringEach((_, path) => (
            HttpStatusCode.OK,
            path switch
            {
                var setting when setting.EndsWith("/config/mediamanagement", StringComparison.Ordinal)
                    => LinksIntoPlace,

                // No root, so these cases stay about the loop rather than about which root a site
                // sits under.
                var roots when roots.EndsWith("/rootfolder", StringComparison.Ordinal) => "[]",
                var listing when listing.EndsWith("/manualimport", StringComparison.Ordinal) => rows,
                _ => "{}",
            }));

        var host = await MonitorHost.CreateAsync(bytes: bytes);
        var studioId = await SeededStudio(host);
        await host.SeedStudioFileAsync(studioId, Later);
        await host.SeedStudioFileAsync(studioId, Earlier);

        var answered = await host.ReflectOwnedViewAsync("studio", studioId);
        Assert.NotNull(answered.JobId);

        var progress = new RecordingJobProgress();
        await host.Jobs.RunLastAsync(progress, TestCt);
        return (host, progress);
    }

    private static Task<int> SeededStudio(MonitorHost host)
        => host.SeedStudioAsync(MonitorHost.StoredEndpoint, MonitorHost.StudioRemoteIdValue);

    private static List<string> Bodies(MonitorHost host)
        => [.. host.Bytes!.Requests.Select(sent => sent.Body).Where(body => body.Length > 0)];

    private static List<JsonObject> Commands(MonitorHost host)
        => [.. Bodies(host).Select(body => JsonNode.Parse(body) as JsonObject).OfType<JsonObject>()];

    private static List<string> CommandNames(MonitorHost host)
        => [.. Commands(host)
            .Select(body => (body["name"] as JsonValue)?.GetValue<string>())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static List<string> ImportModes(MonitorHost host)
        => [.. Commands(host)
            .Select(body => (body["importMode"] as JsonValue)?.GetValue<string>())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
}
