using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>
/// The one decision that decides whether a user's files get duplicated, and what a folder's parsed
/// rows become on the way to the instance.
/// </summary>
/// <remarks>
/// Pure module, no doubles: the hard-link setting arrives as the media-management answer each build
/// returned, the rows as the shape the parse route answers, and the run is driven through delegates
/// that record what they were handed. Nothing here reads a status.
/// </remarks>
public sealed class ReflectOwnedPlannerTests
{
    private const string V3MediaManagementFixture = "whisparr-v3-3.3.8.1097-media-management.json";
    private const string V2MediaManagementFixture = "whisparr-v2-2.2.0.231-media-management.json";

    /// <summary>A parsed row the newer generation matched to a scene it holds.</summary>
    private const string V3MatchedRow = """
        {"id":1,"path":"/config/library/Vixen/scene.mp4","relativePath":"Vixen/scene.mp4",
         "folderName":"Vixen","name":"scene","size":10,
         "movie":{"id":7,"title":"A scene","foreignId":"3c0a6b21-9f7d-4c58-a3e2-71b0d4f5e8a9"},
         "movieFileId":0,"releaseGroup":"","quality":{"quality":{"id":6,"name":"Bluray-1080p"},"revision":{"version":1,"real":0}},
         "languages":[{"id":1,"name":"English"}],"qualityWeight":0,"downloadId":null,
         "customFormats":[],"customFormatScore":0,"indexerFlags":0,"rejections":[]}
        """;

    /// <summary>
    /// A row the parse could not match, exactly as measured: a permanent rejection and NO matched
    /// member at all, rather than a null one.
    /// </summary>
    private const string V3UnmatchedRow = """
        {"id":2,"path":"/config/library/Vixen/unknown.mp4","relativePath":"Vixen/unknown.mp4",
         "folderName":"Vixen","name":"unknown","size":10,"movieFileId":0,"releaseGroup":"",
         "quality":{"quality":{"id":1,"name":"Unknown"},"revision":{"version":1,"real":0}},
         "languages":[{"id":0,"name":"Unknown"}],"qualityWeight":0,"downloadId":null,
         "customFormats":[],"customFormatScore":0,"indexerFlags":0,
         "rejections":[{"reason":"Unknown Movie","type":"permanent"}]}
        """;

    /// <summary>A parsed row the older generation matched to a series and its episodes.</summary>
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

    private static readonly WhisparrGeneration[] Generations =
        [WhisparrGeneration.V3, WhisparrGeneration.V2];

    /// <summary>With the setting on, the answer both builds return as shipped, the planner acts.</summary>
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

    /// <summary>
    /// With the setting off the planner skips, names the reason, and the reason is the SAME value on
    /// both generations: neither offers an import mode that would not duplicate the data.
    /// </summary>
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

    /// <summary>
    /// A setting that is absent, null, of the wrong type, or inside a body that is not the settings
    /// object at all answers skipped and never act.
    /// </summary>
    /// <remarks>
    /// Stricter than the measured default on purpose. Acting on a setting nobody read is how every
    /// matched file gets copied in full with no error.
    /// </remarks>
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

    /// <summary>
    /// A row carrying a permanent rejection and no matched member is left out of the command rather
    /// than sent with a fabricated match, on both generations.
    /// </summary>
    /// <remarks>
    /// The exclusion is on member ABSENCE. The unmatched row is asserted to carry no matched member
    /// at all before it is handed in, because a null check would read a row with a null member the
    /// same way and that is not the shape the parse route answers.
    /// </remarks>
    [Fact]
    public void ARowWithAPermanentRejectionAndNoMatchedMemberIsExcluded()
    {
        Assert.False(((JsonObject)JsonNode.Parse(V3UnmatchedRow)!).ContainsKey("movie"));
        Assert.False(((JsonObject)JsonNode.Parse(V2UnmatchedRow)!).ContainsKey("series"));

        var newer = ReflectOwnedPlanner.Files(
            WhisparrGeneration.V3, $"[{V3MatchedRow},{V3UnmatchedRow}]");
        var older = ReflectOwnedPlanner.Files(
            WhisparrGeneration.V2, $"[{V2UnmatchedRow},{V2MatchedRow}]");

        Assert.NotNull(newer);
        Assert.NotNull(older);
        Assert.Equal(7, ((JsonObject)Assert.Single(newer))["movieId"]!.GetValue<int>());
        Assert.Equal(3, ((JsonObject)Assert.Single(older))["seriesId"]!.GetValue<int>());
        Assert.DoesNotContain("unknown.mp4", newer.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("unknown.mp4", older.ToJsonString(), StringComparison.Ordinal);
    }

    /// <summary>A folder whose parse answers no rows, or only rows nothing matched, composes no command.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("not json")]
    public void AFolderAnsweringNoRowsComposesNoCommand(string? rows)
    {
        Assert.All(Generations, generation => Assert.Null(ReflectOwnedPlanner.Files(generation, rows)));
    }

    [Fact]
    public void AFolderWhoseEveryRowIsUnmatchedComposesNoCommand()
    {
        Assert.Null(ReflectOwnedPlanner.Files(WhisparrGeneration.V3, $"[{V3UnmatchedRow}]"));
        Assert.Null(ReflectOwnedPlanner.Files(WhisparrGeneration.V2, $"[{V2UnmatchedRow}]"));
    }

    /// <summary>
    /// The quality and the languages in every entry are the row's own, byte for byte, and a row
    /// carrying neither is excluded rather than given one.
    /// </summary>
    [Fact]
    public void QualityAndLanguagesComeFromTheRowsAndAreNeverFabricated()
    {
        var row = (JsonObject)JsonNode.Parse(V3MatchedRow)!;
        var entry = (JsonObject)Assert.Single(
            ReflectOwnedPlanner.Files(WhisparrGeneration.V3, $"[{V3MatchedRow}]")!);

        Assert.Equal(row["quality"]!.ToJsonString(), entry["quality"]!.ToJsonString());
        Assert.Equal(row["languages"]!.ToJsonString(), entry["languages"]!.ToJsonString());

        var withoutQuality = (JsonObject)row.DeepClone();
        withoutQuality.Remove("quality");
        var withoutLanguages = (JsonObject)row.DeepClone();
        withoutLanguages.Remove("languages");

        Assert.Null(ReflectOwnedPlanner.Files(WhisparrGeneration.V3, $"[{withoutQuality.ToJsonString()}]"));
        Assert.Null(ReflectOwnedPlanner.Files(WhisparrGeneration.V3, $"[{withoutLanguages.ToJsonString()}]"));
    }

    /// <summary>
    /// Each generation's file entry is spelled as its own interface spells it, transcribed from the
    /// two bundles: the newer names one scene, the older names a series and its episodes.
    /// </summary>
    [Fact]
    public void TheFileEntryIsSpelledPerGenerationAsEachInterfaceSpellsIt()
    {
        var newer = (JsonObject)Assert.Single(
            ReflectOwnedPlanner.Files(WhisparrGeneration.V3, $"[{V3MatchedRow}]")!);
        var older = (JsonObject)Assert.Single(
            ReflectOwnedPlanner.Files(WhisparrGeneration.V2, $"[{V2MatchedRow}]")!);

        Assert.Equal(
            [
                "downloadId", "folderName", "indexerFlags", "languages", "movieFileId", "movieId",
                "path", "quality", "releaseGroup",
            ],
            newer.Select(member => member.Key).Order());
        Assert.Equal(
            [
                "downloadId", "episodeFileId", "episodeIds", "folderName", "indexerFlags", "languages",
                "path", "quality", "releaseGroup", "seriesId",
            ],
            older.Select(member => member.Key).Order());

        Assert.Equal("/config/library/Vixen/scene.mp4", newer["path"]!.GetValue<string>());
        Assert.Equal("Vixen", newer["folderName"]!.GetValue<string>());
        Assert.Equal([41], Assert.IsType<JsonArray>(older["episodeIds"]).Select(id => id!.GetValue<int>()));
        Assert.DoesNotContain("seriesId", newer.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain("movieId", older.ToJsonString(), StringComparison.Ordinal);
    }

    /// <summary>A matched series with no episode named is no match: nothing could be attached.</summary>
    [Fact]
    public void AnOlderGenerationRowNamingNoEpisodeIsExcluded()
    {
        var row = (JsonObject)JsonNode.Parse(V2MatchedRow)!;
        row["episodes"] = new JsonArray();

        Assert.Null(ReflectOwnedPlanner.Files(WhisparrGeneration.V2, $"[{row.ToJsonString()}]"));
    }

    /// <summary>
    /// The command is the manual import in the mode that links when it can, and the mode that moves
    /// the file is not composable.
    /// </summary>
    [Fact]
    public void TheCommandIsAManualImportInCopyModeAndNeverInMoveMode()
    {
        var files = ReflectOwnedPlanner.Files(WhisparrGeneration.V3, $"[{V3MatchedRow}]")!;
        var command = ReflectOwnedPlanner.Command(files);

        Assert.Equal("ManualImport", command["name"]!.GetValue<string>());
        Assert.Equal("copy", command["importMode"]!.GetValue<string>());
        Assert.Equal(["files", "importMode", "name"], command.Select(member => member.Key).Order());
        Assert.Same(files, command["files"]);
        Assert.DoesNotContain("\"move\"", command.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("copy", ReflectOwnedPlanner.ImportMode);
    }

    /// <summary>
    /// A token already cancelled classifies the run as cancelled, reads nothing and attaches nothing.
    /// </summary>
    [Fact]
    public async Task APreCancelledTokenClassifiesTheRunAsCancelledAndNotFailed()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var reads = 0;
        var attaches = 0;

        var run = await ReflectOwnedPlanner.RunAsync(
            WhisparrGeneration.V3,
            Folders("Vixen", "Tushy"),
            (_, _) =>
            {
                reads++;
                return Task.FromResult<string?>($"[{V3MatchedRow}]");
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

    /// <summary>
    /// A run cancelled part-way classifies as cancelled and the folder already attached stays
    /// attached: there is no rollback and none is wanted.
    /// </summary>
    [Fact]
    public async Task ARunCancelledPartWayKeepsWhatWasAlreadyAttached()
    {
        using var cancellation = new CancellationTokenSource();
        var attached = new List<JsonArray>();

        var run = await ReflectOwnedPlanner.RunAsync(
            WhisparrGeneration.V3,
            Folders("Vixen", "Tushy", "Blacked"),
            (_, _) => Task.FromResult<string?>($"[{V3MatchedRow}]"),
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

    /// <summary>
    /// Each folder's rows are read once and handed into exactly one attach, and nothing from one
    /// folder reaches the next one's command.
    /// </summary>
    [Fact]
    public async Task EachFolderIsReadOnceAndHandedIntoOneCommandAndNothingCarriesAcross()
    {
        var read = new List<string>();
        var attached = new List<JsonArray>();

        var run = await ReflectOwnedPlanner.RunAsync(
            WhisparrGeneration.V3,
            Folders("/config/library/Vixen", "/config/library/Tushy", "/config/library/Empty"),
            (folder, _) => Task.FromResult<string?>(
                folder.EndsWith("Empty", StringComparison.Ordinal)
                    ? "[]"
                    : $"[{V3MatchedRow.Replace("/config/library/Vixen", folder, StringComparison.Ordinal)}]"),
            (files, _) =>
            {
                attached.Add(files);
                return Task.FromResult(attached.Count == 1);
            },
            TestContext.Current.CancellationToken);

        // The read is per folder, so reads are counted through the attach delegate's inputs below
        // and the run's own counts.
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

    private static async IAsyncEnumerable<string> Folders(params string[] folders)
    {
        foreach (var folder in folders)
        {
            yield return folder;
        }

        await Task.CompletedTask;
    }
}
