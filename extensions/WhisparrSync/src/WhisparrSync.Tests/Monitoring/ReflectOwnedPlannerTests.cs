using System.Text.Json.Nodes;
using WhisparrSync.Addressing;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

public sealed class ReflectOwnedPlannerTests
{
    private const string V3MediaManagementFixture = "whisparr-v3-3.3.8.1097-media-management.json";
    private const string V2MediaManagementFixture = "whisparr-v2-2.2.0.231-media-management.json";

    private const string V3MatchedRow = """
        {"id":1,"path":"/config/library/Vixen/scene.mp4","relativePath":"Vixen/scene.mp4",
         "folderName":"Vixen","name":"scene","size":10,
         "movie":{"id":7,"title":"A scene","foreignId":"3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9"},
         "movieFileId":0,"releaseGroup":"","quality":{"quality":{"id":6,"name":"Bluray-1080p"},"revision":{"version":1,"real":0}},
         "languages":[{"id":1,"name":"English"}],"qualityWeight":0,"downloadId":null,
         "customFormats":[],"customFormatScore":0,"indexerFlags":0,"rejections":[]}
        """;

    // A row the parse could not match, as measured: a permanent rejection, and no matched member
    // at all rather than a null one.
    private const string V3UnmatchedRow = """
        {"id":2,"path":"/config/library/Vixen/unknown.mp4","relativePath":"Vixen/unknown.mp4",
         "folderName":"Vixen","name":"unknown","size":10,"movieFileId":0,"releaseGroup":"",
         "quality":{"quality":{"id":1,"name":"Unknown"},"revision":{"version":1,"real":0}},
         "languages":[{"id":0,"name":"Unknown"}],"qualityWeight":0,"downloadId":null,
         "customFormats":[],"customFormatScore":0,"indexerFlags":0,
         "rejections":[{"reason":"Unknown Movie","type":"permanent"}]}
        """;

    private const string V2MatchedRow = """
        {"id":1,"path":"/config/library/Vixen/scene.mp4","relativePath":"Vixen/scene.mp4",
         "folderName":"Vixen","name":"scene","size":10,
         "series":{"id":3,"title":"Vixen"},"episodes":[{"id":41,"title":"A scene"}],
         "episodeFileId":0,"releaseGroup":"","quality":{"quality":{"id":6,"name":"Bluray-1080p"},"revision":{"version":1,"real":0}},
         "languages":[{"id":1,"name":"English"}],"qualityWeight":0,"downloadId":null,
         "customFormats":[],"customFormatScore":0,"indexerFlags":0,"rejections":[]}
        """;

    private const string V2UnmatchedRow = """
        {"id":2,"path":"/config/library/Vixen/unknown.mp4","relativePath":"Vixen/unknown.mp4",
         "folderName":"Vixen","name":"unknown","size":10,"episodeFileId":0,"releaseGroup":"",
         "quality":{"quality":{"id":1,"name":"Unknown"},"revision":{"version":1,"real":0}},
         "languages":[{"id":0,"name":"Unknown"}],"qualityWeight":0,"downloadId":null,
         "customFormats":[],"customFormatScore":0,"indexerFlags":0,
         "rejections":[{"reason":"Unknown Series","type":"permanent"}]}
        """;

    // A file in the inbox matched to a site registered under another declared root. This is the
    // arrangement measured to copy the bytes in full.
    private const string InboxRowMatchedToRootA = """
        {"path":"/library/inbox/Tushy - 2015-05-04 - Big Butt 1080p WEBDL.mp4",
         "relativePath":"Tushy - 2015-05-04 - Big Butt 1080p WEBDL.mp4","folderName":"inbox","size":72246,
         "series":{"id":2,"title":"Tushy","path":"/library/rootA/Tushy"},
         "episodes":[{"id":211,"title":"Big Butt"}],"episodeFileId":0,"releaseGroup":"",
         "quality":{"quality":{"id":6,"name":"Bluray-1080p"},"revision":{"version":1,"real":0}},
         "languages":[{"id":1,"name":"English"}],"indexerFlags":0,"downloadId":null,"rejections":[]}
        """;

    private const string SecondInboxRowMatchedToRootA = """
        {"path":"/library/inbox/Tushy - 2015-05-11 - Second 1080p WEBDL.mp4",
         "relativePath":"Tushy - 2015-05-11 - Second 1080p WEBDL.mp4","folderName":"inbox","size":72246,
         "series":{"id":2,"title":"Tushy","path":"/library/rootA/Tushy"},
         "episodes":[{"id":212,"title":"Second"}],"episodeFileId":0,"releaseGroup":"",
         "quality":{"quality":{"id":6,"name":"Bluray-1080p"},"revision":{"version":1,"real":0}},
         "languages":[{"id":1,"name":"English"}],"indexerFlags":0,"downloadId":null,"rejections":[]}
        """;

    private const string RootARowMatchedToRootA = """
        {"path":"/library/rootA/Tushy/scene.mp4","relativePath":"scene.mp4","folderName":"Tushy","size":72246,
         "series":{"id":2,"title":"Tushy","path":"/library/rootA/Tushy"},
         "episodes":[{"id":213,"title":"A scene"}],"episodeFileId":0,"releaseGroup":"",
         "quality":{"quality":{"id":6,"name":"Bluray-1080p"},"revision":{"version":1,"real":0}},
         "languages":[{"id":1,"name":"English"}],"indexerFlags":0,"downloadId":null,"rejections":[]}
        """;

    // The roots the measured instance declares. They nest, so the most specific one has to answer
    // for a path; taking the first would see one root where there are three.
    private static readonly string[] DeclaredRoots = ["/library", "/library/rootA", "/library/rootB"];

    private static readonly WhisparrGeneration[] Generations =
        [WhisparrGeneration.V3, WhisparrGeneration.V2];

    [Fact]
    public void HardLinksOnAnswersActOnBothGenerations()
    {
        foreach (var fixture in new[] { V3MediaManagementFixture, V2MediaManagementFixture })
        {
            var decision = ReflectOwnedPlanner.Decide(ProbeFixtures.Read(fixture));

            Assert.True(decision.Act);
            Assert.Null(decision.Reason);
        }
    }

    [Fact]
    public void HardLinksOffAnswersSkippedWithTheSameReasonOnBothGenerations()
    {
        var reasons = new[] { V3MediaManagementFixture, V2MediaManagementFixture }
            .Select(fixture =>
            {
                var settings = (JsonObject)JsonNode.Parse(ProbeFixtures.Read(fixture))!;
                Assert.True(settings[ReflectOwnedPlanner.HardLinkSetting]!.GetValue<bool>());
                settings[ReflectOwnedPlanner.HardLinkSetting] = false;
                return ReflectOwnedPlanner.Decide(settings.ToJsonString());
            })
            .ToList();

        Assert.All(reasons, decision => Assert.False(decision.Act));
        Assert.Equal(
            [ReflectOwnedSkipReason.HardLinksOff, ReflectOwnedSkipReason.HardLinksOff],
            reasons.Select(decision => decision.Reason));
    }

    // Stricter than the measured default. Acting on a setting nobody read copies every matched file
    // in full with no error.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("""{"copyUsingHardlinks":null}""")]
    [InlineData("""{"copyUsingHardlinks":"true"}""")]
    [InlineData("""{"copyUsingHardlinks":1}""")]
    [InlineData("""{"copyUsingHardLinks":true}""")]
    public void AnAbsentOrUnreadableSettingAnswersSkippedAndNotAct(string? body)
    {
        var decision = ReflectOwnedPlanner.Decide(body);

        Assert.False(decision.Act);
        Assert.Equal(ReflectOwnedSkipReason.HardLinkSettingUnreadable, decision.Reason);
    }

    // The exclusion keys on member absence. The unmatched rows are asserted to carry no matched
    // member at all, because a null check would read a row with a null member the same way and that
    // is not the shape the parse route answers.
    [Fact]
    public void ARowWithAPermanentRejectionAndNoMatchedMemberIsExcluded()
    {
        Assert.False(((JsonObject)JsonNode.Parse(V3UnmatchedRow)!).ContainsKey("movie"));
        Assert.False(((JsonObject)JsonNode.Parse(V2UnmatchedRow)!).ContainsKey("series"));

        var v3 = Entries(
            WhisparrGeneration.V3, $"[{V3MatchedRow},{V3UnmatchedRow}]");
        var v2 = Entries(
            WhisparrGeneration.V2, $"[{V2UnmatchedRow},{V2MatchedRow}]");

        Assert.NotNull(v3);
        Assert.NotNull(v2);
        Assert.Equal(7, Assert.IsType<JsonObject>(Assert.Single(v3))["movieId"]!.GetValue<int>());
        Assert.Equal(3, Assert.IsType<JsonObject>(Assert.Single(v2))["seriesId"]!.GetValue<int>());
        Assert.DoesNotContain("unknown.mp4", v3.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unknown.mp4", v2.ToJsonString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("not json")]
    public void AFolderAnsweringNoRowsComposesNoCommand(string? rows)
    {
        Assert.All(Generations, generation => Assert.Null(Entries(generation, rows)));
    }

    [Fact]
    public void AFolderWhoseEveryRowIsUnmatchedComposesNoCommand()
    {
        Assert.Null(Entries(WhisparrGeneration.V3, $"[{V3UnmatchedRow}]"));
        Assert.Null(Entries(WhisparrGeneration.V2, $"[{V2UnmatchedRow}]"));
    }

    [Fact]
    public void QualityAndLanguagesComeFromTheRowsAndAreNeverFabricated()
    {
        var row = (JsonObject)JsonNode.Parse(V3MatchedRow)!;
        var entry = Assert.IsType<JsonObject>(Assert.Single(
            Entries(WhisparrGeneration.V3, $"[{V3MatchedRow}]")!));

        Assert.Equal(row["quality"]!.ToJsonString(), entry["quality"]!.ToJsonString());
        Assert.Equal(row["languages"]!.ToJsonString(), entry["languages"]!.ToJsonString());

        var withoutQuality = (JsonObject)row.DeepClone();
        withoutQuality.Remove("quality");
        var withoutLanguages = (JsonObject)row.DeepClone();
        withoutLanguages.Remove("languages");

        Assert.Null(Entries(WhisparrGeneration.V3, $"[{withoutQuality.ToJsonString()}]"));
        Assert.Null(Entries(WhisparrGeneration.V3, $"[{withoutLanguages.ToJsonString()}]"));
    }

    [Fact]
    public void TheFileEntryIsSpelledPerGenerationAsEachInterfaceSpellsIt()
    {
        var v3 = Assert.IsType<JsonObject>(Assert.Single(
            Entries(WhisparrGeneration.V3, $"[{V3MatchedRow}]")!));
        var v2 = Assert.IsType<JsonObject>(Assert.Single(
            Entries(WhisparrGeneration.V2, $"[{V2MatchedRow}]")!));

        Assert.Equal(
            [
                "downloadId", "folderName", "indexerFlags", "languages", "movieFileId", "movieId",
                "path", "quality", "releaseGroup",
            ],
            v3.Select(member => member.Key).Order());
        Assert.Equal(
            [
                "downloadId", "episodeFileId", "episodeIds", "folderName", "indexerFlags", "languages",
                "path", "quality", "releaseGroup", "seriesId",
            ],
            v2.Select(member => member.Key).Order());

        Assert.Equal("/config/library/Vixen/scene.mp4", v3["path"]!.GetValue<string>());
        Assert.Equal("Vixen", v3["folderName"]!.GetValue<string>());
        Assert.Equal([41], Assert.IsType<JsonArray>(v2["episodeIds"]).Select(id => id!.GetValue<int>()));
        Assert.DoesNotContain("seriesId", v3.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("movieId", v2.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderGenerationRowNamingNoEpisodeIsExcluded()
    {
        var row = (JsonObject)JsonNode.Parse(V2MatchedRow)!;
        row["episodes"] = new JsonArray();

        Assert.Null(Entries(WhisparrGeneration.V2, $"[{row.ToJsonString()}]"));
    }

    [Fact]
    public void TheCommandIsAManualImportInCopyModeAndNeverInMoveMode()
    {
        var files = Entries(WhisparrGeneration.V3, $"[{V3MatchedRow}]")!;
        var command = ReflectOwnedPlanner.Command(files);

        Assert.Equal("ManualImport", command["name"]!.GetValue<string>());
        Assert.Equal("copy", command["importMode"]!.GetValue<string>());
        Assert.Equal(["files", "importMode", "name"], command.Select(member => member.Key).Order());
        Assert.Same(files, command["files"]);
        Assert.DoesNotContain("\"move\"", command.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("copy", ReflectOwnedPlanner.ImportMode);
    }

    // The import mode that links copies the whole file when the two are not on one filesystem, with
    // no error and no distinct outcome.
    [Fact]
    public void AFileWhoseSiteSitsUnderAnotherDeclaredRootReachesNoCommand()
    {
        var planned = ReflectOwnedPlanner.Files(
            WhisparrGeneration.V2, $"[{InboxRowMatchedToRootA}]", DeclaredRoots);

        Assert.Null(planned.Entries);
        Assert.Equal(1, planned.LeftUnderAnotherRoot);
    }

    [Fact]
    public void AFolderMixingBothKindsSendsOnlyTheEntriesWhoseSiteSharesTheirRoot()
    {
        var planned = ReflectOwnedPlanner.Files(
            WhisparrGeneration.V2,
            $"[{InboxRowMatchedToRootA},{RootARowMatchedToRootA}]",
            DeclaredRoots);

        var command = ReflectOwnedPlanner.Command(planned.Entries!).ToJsonString();

        Assert.Equal(1, planned.LeftUnderAnotherRoot);
        Assert.Contains("/library/rootA/Tushy/scene.mp4", command, StringComparison.Ordinal);
        Assert.DoesNotContain("/library/inbox", command, StringComparison.Ordinal);
    }

    // Refusing on an unknown root would stop every import on an instance whose roots could not be
    // read.
    [Fact]
    public void AnInstanceDeclaringNoRootSendsEveryEntry()
    {
        var planned = ReflectOwnedPlanner.Files(
            WhisparrGeneration.V2, $"[{InboxRowMatchedToRootA}]", []);

        Assert.Single(planned.Entries!);
        Assert.Equal(0, planned.LeftUnderAnotherRoot);
    }

    // A site with no path of its own leaves the destination unknown, not known to be elsewhere.
    [Fact]
    public void ARowWhoseSiteCarriesNoPathIsSent()
    {
        var row = (JsonObject)JsonNode.Parse(InboxRowMatchedToRootA)!;
        ((JsonObject)row["series"]!).Remove("path");

        var planned = ReflectOwnedPlanner.Files(
            WhisparrGeneration.V2, $"[{row.ToJsonString()}]", DeclaredRoots);

        Assert.Single(planned.Entries!);
        Assert.Equal(0, planned.LeftUnderAnotherRoot);
    }

    [Fact]
    public void ARowNamingNoFileIsExcludedAndIsNotCountedAsLeftUnderAnotherRoot()
    {
        var row = (JsonObject)JsonNode.Parse(InboxRowMatchedToRootA)!;
        row.Remove("path");

        var planned = ReflectOwnedPlanner.Files(
            WhisparrGeneration.V2, $"[{row.ToJsonString()}]", DeclaredRoots);

        Assert.Null(planned.Entries);
        Assert.Equal(0, planned.LeftUnderAnotherRoot);
    }

    // The instance declined nothing and was never asked, so counting the folder as refused would
    // say it answered.
    [Fact]
    public async Task ARunLeavingEveryRowUnderAnotherRootCountsThemAndAttachesNothing()
    {
        var attaches = 0;

        var run = await ReflectOwnedPlanner.RunAsync(
            WhisparrGeneration.V2,
            DeclaredRoots,
            Folders("/library/inbox", "/library/inbox/second"),
            OnTheInstance,
            (_, _) => Task.FromResult(
                ImportableListing.Listed($"[{InboxRowMatchedToRootA},{SecondInboxRowMatchedToRootA}]")),
            (_, _) =>
            {
                attaches++;
                return Task.FromResult(true);
            },
            TestCt);

        Assert.Equal(0, attaches);
        Assert.Equal(4, run.EntriesLeftUnderAnotherRoot);
        Assert.Equal(0, run.FoldersAttached);
        Assert.Equal(0, run.FoldersRefused);
    }

    [Fact]
    public async Task APreCancelledTokenClassifiesTheRunAsCancelledAndNotFailed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var reads = 0;
        var attaches = 0;

        var run = await ReflectOwnedPlanner.RunAsync(
            WhisparrGeneration.V3,
            [],
            Folders("Vixen", "Tushy"),
            OnTheInstance,
            (_, _) =>
            {
                reads++;
                return Task.FromResult(ImportableListing.Listed($"[{V3MatchedRow}]"));
            },
            (_, _) =>
            {
                attaches++;
                return Task.FromResult(true);
            },
            cancelled.Token);

        Assert.Equal(ReflectOwnedRunOutcome.Cancelled, run.Outcome);
        Assert.Equal(0, run.FoldersAttached);
        Assert.Equal(0, reads);
        Assert.Equal(0, attaches);
    }

    [Fact]
    public async Task ARunCancelledPartWayKeepsWhatWasAlreadyAttached()
    {
        using var cancellation = new CancellationTokenSource();
        var attached = new List<JsonArray>();

        var run = await ReflectOwnedPlanner.RunAsync(
            WhisparrGeneration.V3,
            [],
            Folders("Vixen", "Tushy", "Blacked"),
            OnTheInstance,
            (_, _) => Task.FromResult(ImportableListing.Listed($"[{V3MatchedRow}]")),
            (files, _) =>
            {
                attached.Add(files);
                cancellation.Cancel();
                return Task.FromResult(true);
            },
            cancellation.Token);

        Assert.Equal(ReflectOwnedRunOutcome.Cancelled, run.Outcome);
        Assert.Equal(1, run.FoldersAttached);
        Assert.Single(attached);
    }

    [Fact]
    public async Task EachFolderIsReadOnceAndHandedIntoOneCommandAndNothingCarriesAcross()
    {
        var read = new List<string>();
        var attached = new List<JsonArray>();

        var run = await ReflectOwnedPlanner.RunAsync(
            WhisparrGeneration.V3,
            [],
            Folders("/config/library/Vixen", "/config/library/Tushy", "/config/library/Empty"),
            OnTheInstance,
            (folder, _) => Task.FromResult(
                ImportableListing.Listed(
                    folder.EndsWith("Empty", StringComparison.Ordinal)
                        ? "[]"
                        : $"[{V3MatchedRow.Replace("/config/library/Vixen", folder, StringComparison.Ordinal)}]")),
            (files, _) =>
            {
                attached.Add(files);
                return Task.FromResult(attached.Count == 1);
            },
            TestContext.Current.CancellationToken);

        // The read is per folder, so reads are counted from the attach delegate's inputs.
        read.AddRange(attached.Select(files => ((JsonObject)files[0]!)["path"]!.GetValue<string>()));

        Assert.Equal(ReflectOwnedRunOutcome.Completed, run.Outcome);
        Assert.Equal(2, attached.Count);
        Assert.Equal(1, run.FoldersAttached);
        Assert.Equal(1, run.FoldersRefused);
        Assert.Equal(
            ["/config/library/Tushy/scene.mp4", "/config/library/Vixen/scene.mp4"],
            read.Order());
        Assert.All(attached, files => Assert.Single(files));
    }

    [Fact]
    public async Task AFolderThatCannotBeAddressedIsNeverReadAndIsCountedOnItsOwn()
    {
        var read = new List<string>();

        var run = await ReflectOwnedPlanner.RunAsync(
            WhisparrGeneration.V3,
            [],
            Folders("G:/Downloads/P/Vixen", "G:/Downloads/P/Tushy"),
            (_, _) => Task.FromResult(Unaddressable),
            (folder, _) =>
            {
                read.Add(folder);
                return Task.FromResult(ImportableListing.Listed($"[{V3MatchedRow}]"));
            },
            (_, _) => Task.FromResult(true),
            TestCt);

        Assert.Empty(read);
        Assert.Equal(2, run.FoldersNotAddressed);
        Assert.Equal(0, run.FoldersRefused);
        Assert.Equal(0, run.FoldersAttached);
    }

    [Fact]
    public async Task ARunReportsOneRefusalPerLibraryRootRatherThanPerFolder()
    {
        var run = await ReflectOwnedPlanner.RunAsync(
            WhisparrGeneration.V3,
            [],
            Folders("G:/Downloads/P/Vixen", "G:/Downloads/P/Tushy"),
            (_, _) => Task.FromResult(Unaddressable),
            (_, _) => Task.FromResult(ImportableListing.Listed($"[{V3MatchedRow}]")),
            (_, _) => Task.FromResult(true),
            TestCt);

        var only = Assert.Single(run.AddressRefusals!);
        Assert.Equal("G:/Downloads/P", only.CoveRoot);
        Assert.Equal(FolderAgreementRefusal.NothingResolved, only.Refusal);
        Assert.Equal(["/data/Vixen"], only.Tried);
    }

    [Fact]
    public async Task TheListingIsAskedForThePathTheAddressAnswered()
    {
        var read = new List<string>();

        await ReflectOwnedPlanner.RunAsync(
            WhisparrGeneration.V3,
            [],
            Folders("G:/Downloads/P/Vixen"),
            (folder, _) => Task.FromResult(
                new AddressedFolder(
                    folder.Replace("G:/Downloads/P", "/data", StringComparison.Ordinal),
                    null,
                    "G:/Downloads/P",
                    [])),
            (folder, _) =>
            {
                read.Add(folder);
                return Task.FromResult(ImportableListing.Listed($"[{V3MatchedRow}]"));
            },
            (_, _) => Task.FromResult(true),
            TestCt);

        Assert.Equal(["/data/Vixen"], read);
    }

    // The cases using this are about the loop rather than the addressing, so the instance spells a
    // folder the way the library does.
    private static Task<AddressedFolder> OnTheInstance(string folder, CancellationToken _)
        => Task.FromResult(new AddressedFolder(folder, null, "/config/library", []));

    private static AddressedFolder Unaddressable { get; } = new(
        null, FolderAgreementRefusal.NothingResolved, "G:/Downloads/P", ["/data/Vixen"]);

    private static CancellationToken TestCt => TestContext.Current.CancellationToken;

    private static JsonArray? Entries(WhisparrGeneration generation, string? rows)
        => ReflectOwnedPlanner.Files(generation, rows, []).Entries;

    private static async IAsyncEnumerable<string> Folders(params string[] folders)
    {
        foreach (var folder in folders)
        {
            yield return folder;
        }

        await Task.CompletedTask;
    }
}
