using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// Two guarantees about reflect owned, asserted over the path a user reaches: it never transfers
/// file data, and neither it nor the gesture that starts it asks the reader for anything.
/// </summary>
/// <remarks>
/// Both were previously safe for a reason that no longer holds. The verb had no mounted route, so
/// nothing it might compose could reach an instance and the question could be answered by reading
/// the source. It is mounted now, and it runs by itself when monitoring is turned on, so each is
/// asserted here on the bytes that actually leave and on the shape of what the route accepts.
/// <para>
/// The transfer assertions read the composed command rather than the constant it is composed from.
/// Comparing a constant against itself is not evidence of anything; what an instance acts on is the
/// serialized body, which is composed below the seam a call site can see.
/// </para>
/// <para>
/// Both generations report exactly two usable import modes, and the one this product never composes
/// moves the file out of the library. The captured mode lists are what that is read from.
/// </para>
/// </remarks>
public sealed class ReflectOwnedNeverTransfersTests
{
    /// <summary>The mode that links when it can, which is the only one this product composes.</summary>
    private const string Linking = "copy";

    /// <summary>The only other mode either instance offers. It moves the file out of the library.</summary>
    private const string Moving = "move";

    /// <summary>The one command name this whole path may name.</summary>
    private const string TheOnlyCommand = "ManualImport";

    private const string LinksIntoPlace = """{"copyUsingHardlinks":true}""";

    private const string CopiesInstead = """{"copyUsingHardlinks":false}""";

    /// <summary>One folder's parse answer, with everything an attach has to be composed from.</summary>
    private const string Attachable = """
        [{"path":"/library/vixen/2026/scene.mp4","folderName":"2026",
          "quality":{"quality":{"id":7}},"languages":[{"id":1}],"movie":{"id":31}}]
        """;

    /// <summary>
    /// The same rows with the quality gone, which the instance's own submit path refuses.
    /// </summary>
    /// <remarks>
    /// A row the parse could not match carries no matched member at all rather than a null one, so
    /// exclusion is on absence and a row this shape must contribute nothing rather than be filled in.
    /// </remarks>
    private const string Unmatchable = """
        [{"path":"/library/vixen/2026/scene.mp4","folderName":"2026","movie":{"id":31}}]
        """;

    private const string Earlier = "/library/vixen/2025";
    private const string Later = "/library/vixen/2026";

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    /// <summary>
    /// A run over two folders composes one linking import per folder and moves nothing.
    /// </summary>
    [Fact]
    public async Task ARunOverTwoFoldersComposesOneLinkingImportPerFolder()
    {
        await using var host = (await RunOverAsync(Attachable)).Host;
        var commands = Commands(host);

        Assert.Equal(2, commands.Count);
        Assert.All(commands, body => Assert.Equal(TheOnlyCommand, body["name"]!.GetValue<string>()));
        Assert.All(commands, body => Assert.Equal(Linking, body["importMode"]!.GetValue<string>()));
    }

    /// <summary>
    /// No body the whole path sends carries the mode that moves the file, at any depth.
    /// </summary>
    /// <remarks>
    /// Asserted over EVERY recorded body rather than over the command bodies alone: the mode reaching
    /// an instance is not decided by which request this product thinks composed it.
    /// </remarks>
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

    /// <summary>
    /// The whole path names one command and no other, and retracts nothing.
    /// </summary>
    /// <remarks>
    /// The command names are read out of the bodies rather than checked against a list of forbidden
    /// ones. A denylist covers the acts whoever wrote it thought of; an allow-set of exactly one name
    /// covers a rename, an organize, a delete and a search alike, including one nobody has written
    /// down. The transcribed grabbing names are asserted beside it, because those are the ones a
    /// reader will look for.
    /// </remarks>
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

    /// <summary>
    /// A folder whose rows cannot be matched contributes no request and is neither linked nor refused.
    /// </summary>
    /// <remarks>
    /// The refused count matters as much as the linked one: reporting an unmatched folder as refused
    /// would say the instance declined something it was never sent.
    /// </remarks>
    [Fact]
    public async Task AFolderWhoseRowsCannotBeMatchedSendsNothingAndIsNotCountedEitherWay()
    {
        var (driven, progress) = await RunOverAsync(Unmatchable);
        await using var host = driven;

        Assert.Empty(Commands(host));
        Assert.Contains(progress.Reports, report => report.SubTask == "0 linked, 0 refused.");
    }

    /// <summary>
    /// With the hard-link setting off the route makes exactly one request, and it is the read.
    /// </summary>
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

    /// <summary>
    /// The monitor gesture asks the reader for nothing, and cannot be given a folder or a profile.
    /// </summary>
    /// <remarks>
    /// Asserted by reflection over the request contract as well as behaviourally, so a folder or a
    /// profile member added later fails here rather than at whichever surface first sends one.
    /// </remarks>
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

    /// <summary>
    /// The automatic run adds no prompt and no wait: the click reads no folder and no profile.
    /// </summary>
    /// <remarks>
    /// Driven over an entity the instance ALREADY holds, which is the branch a profile or root-folder
    /// read would be pointless on and the one a careless add would reach anyway.
    /// </remarks>
    [Fact]
    public async Task TheAutomaticRunAddsNoReadToTheClickThatStartsIt()
    {
        await using var host = await MonitorHost.CreateAsync();
        host.Client
            .Answering(
                nameof(IWhisparrReflectOwnedActing.ReadHardlinkSettingAsync),
                MonitorHost.Json(200, LinksIntoPlace))
            .Answering(
                nameof(IWhisparrStudioActing.ReadStudioAsync),
                MonitorHost.Json(200, """{"id":9,"monitored":false}"""));
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

    /// <summary>
    /// One host whose reflect-owned run has been driven to completion over two folders, with what
    /// the run reported.
    /// </summary>
    /// <remarks>
    /// The answers are positional, and the order is the path's own: the route's setting read, the
    /// run's own read of the same setting, then one parse and one command per folder.
    /// </remarks>
    private static async Task<(MonitorHost Host, RecordingJobProgress Progress)> RunOverAsync(
        string rows)
    {
        var bytes = BodyRecordingHandler.AnsweringInTurn(
            (HttpStatusCode.OK, LinksIntoPlace),
            (HttpStatusCode.OK, LinksIntoPlace),
            (HttpStatusCode.OK, rows),
            (HttpStatusCode.OK, "{}"),
            (HttpStatusCode.OK, rows),
            (HttpStatusCode.OK, "{}"));

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

    /// <summary>Every request body that actually left, as text.</summary>
    private static List<string> Bodies(MonitorHost host)
        => [.. host.Bytes!.Requests.Select(sent => sent.Body).Where(body => body.Length > 0)];

    /// <summary>Every command body that actually left, parsed.</summary>
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
