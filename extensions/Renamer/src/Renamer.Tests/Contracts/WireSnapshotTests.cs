using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Renamer.Api;
using Renamer.Contracts;
using Renamer.Planner;

namespace Renamer.Tests.Contracts;

/// <summary>
/// Pins the exact bytes every response DTO and status enum serializes to under
/// <c>CoveJsonOptions.WebWithEnumStrings()</c> — the configuration each wire result is built from
/// (camelCase properties, camelCase string enum values). A regression that drops the
/// <see cref="JsonStringEnumConverter"/> or changes the naming policy flips a fixture red.
/// <para>
/// Three things keep this class earning its place now that the TypeScript wire types are generated.
/// Its expectations are transcribed by hand from real serialized bytes, so it is the one wire check
/// here that does not derive from the emitted document — the document, its schemas and the generated
/// types all descend from a single source, and everything descending from a wrong source agrees with
/// the mistake. Its committed fixtures are the evidence that moving every response onto one serializer
/// changed no output. And the failure it guards stays reachable: nothing stops that shared
/// configuration from losing its converter or its naming policy, which would leave the document and
/// the generated types untouched while every enum on the wire moved.
/// </para>
/// <para>
/// What it does not cover is WHICH options instance a handler hands its result. <c>WireJsonResponseTests</c>
/// reads the bytes a result actually writes, and that is the check for this one's blind spot.
/// </para>
/// The response enums (<see cref="RenamerStatus"/> / <see cref="RenamerFileKind"/> /
/// <see cref="ConfirmLevel"/>) emit camelCase values; the persisted <c>RenamerOptions</c> blob keeps its
/// own PascalCase spelling independently. Set <c>WIRE_SNAPSHOT_UPDATE=1</c> to (re)write the fixtures.
/// Bare-safe.
/// </summary>
[Trait("Tier", "L0")]
public sealed class WireSnapshotTests
{
    // Web is test-local because it serializes the synthetic snapshot envelopes and the RenamerRequest
    // body, which no product response endpoint emits; the DTO responses ride an instance of the same
    // configuration PreviewResponseJsonOptions is built from.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions PreviewResponse_ = PreviewContracts.PreviewResponseJsonOptions;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public void StatusEnums_WireStrings()
    {
        var snapshot = new
        {
            renamerStatus = Enum.GetValues<RenamerStatus>()
                .ToDictionary(v => v.ToString(), v => JsonSerializer.Serialize(v, PreviewResponse_)),
            renamerFileKind = Enum.GetValues<RenamerFileKind>()
                .ToDictionary(v => v.ToString(), v => JsonSerializer.Serialize(v, PreviewResponse_)),
        };
        AssertSnapshot("status-enums", JsonSerializer.Serialize(snapshot, Web));
    }

    [Fact]
    public void RenamerRequest_CamelCase()
    {
        AssertSnapshot("renamer-request",
            JsonSerializer.Serialize(new RenamerRequest("video", [1, 2, 3]), Web));
    }

    [Fact]
    public void PreviewResponse_ItemsAndSummary()
    {
        var items = new List<RenamerPlanItem>
        {
            new(FileId: 10, OldFullPath: "/lib/raw one.mkv", NewFullPath: "/lib/First Film.mkv",
                Status: RenamerStatus.Renamer, NewBasename: "First Film.mkv", TargetFolderPath: "/lib",
                Suffixed: false, Sanitized: true, MatchedRule: "InPlace", TargetVolume: "/"),
            new(FileId: 11, OldFullPath: "/lib/x.mkv", NewFullPath: "/lib/x.mkv",
                Status: RenamerStatus.NoOp, NewBasename: "x.mkv", TargetFolderPath: "/lib",
                Reason: "already matches"),
        };
        var summary = new PreviewSummary(
            TotalCount: 1, SameVolumeCount: 1, CrossVolumeCount: 0, CrossVolumeBytes: 0,
            VolumePairs: [], ConfirmLevel: ConfirmLevel.Light, Undoable: true);
        AssertSnapshot("preview-response",
            JsonSerializer.Serialize(
                new PreviewResponse([.. items.Select(PreviewItemView.From)], summary), PreviewResponse_));
    }

    [Fact]
    public void ScanRow_FlattenedWithKind_AndNoDerivableFields()
    {
        var item = new RenamerPlanItem(
            FileId: 42, OldFullPath: "/lib/a.mp3", NewFullPath: "/music/Artist - Song.mp3",
            Status: RenamerStatus.Move, NewBasename: "Artist - Song.mp3", TargetFolderPath: "/music",
            ResolvedDestinationRoot: "/music", MatchedRule: "Studio:7(direct)", TargetVolume: "/");
        string json = JsonSerializer.Serialize(
            ScanRow.From(RenamerFileKind.Audio, entityId: 5, item), PreviewResponse_);

        // The three consumer-less fields and the two the client derives from newFullPath must be ABSENT:
        // this row multiplies by library size, so a field nobody reads is weight on every page.
        foreach (var dropped in new[]
        {
            "resolvedDestinationRoot", "matchedRule", "targetVolume", "newBasename", "targetFolderPath",
        })
        {
            Assert.DoesNotContain(dropped, json, StringComparison.Ordinal);
        }

        AssertSnapshot("scan-row", json);
    }

    [Fact]
    public void ScanSummaryView_CamelCase_PopulatedAndTruncated()
    {
        static ScanKindSummary Kind(RenamerFileKind kind, int files, bool truncated) => new(
            kind, Entities: files, Files: files,
            StatusCounts:
            [
                new ScanStatusCount(RenamerStatus.Renamer, files - 1),
                new ScanStatusCount(RenamerStatus.SkipNoSpace, 1),
            ],
            BlastRadius: new PreviewSummary(
                TotalCount: files, SameVolumeCount: 0, CrossVolumeCount: files, CrossVolumeBytes: 4096,
                VolumePairs: [new VolumePairDelta("/src", "/dest", files, 4096)],
                ConfirmLevel: ConfirmLevel.Heavy, Undoable: true),
            VolumePairsTruncated: truncated);

        var populated = new ScanSummary(
            ScanSummary.CurrentSchemaVersion, 638000000000000000L,
            [Kind(RenamerFileKind.Video, 3, truncated: false)]);
        var truncated = new ScanSummary(
            ScanSummary.CurrentSchemaVersion, 638000000000000000L,
            [Kind(RenamerFileKind.Image, 2, truncated: true)]);

        var snapshot = new
        {
            populated = ScanSummaryView.From(populated, [RenamerFileKind.Video]),
            truncatedPairs = ScanSummaryView.From(truncated, [RenamerFileKind.Image]),
            noReadableKind = ScanSummaryView.From(populated, [RenamerFileKind.Audio]),
        };
        AssertSnapshot("scan-summary-view", JsonSerializer.Serialize(snapshot, PreviewResponse_));
    }

    [Fact]
    public void ScanRowsPage_CamelCase_WithAndWithoutACursor()
    {
        var row = ScanRow.From(RenamerFileKind.Video, entityId: 9, new RenamerPlanItem(
            FileId: 90, OldFullPath: "/lib/raw.mkv", NewFullPath: "/lib/Title.mkv",
            Status: RenamerStatus.Renamer, NewBasename: "Title.mkv", TargetFolderPath: "/lib",
            Suffixed: true, Sanitized: true));

        var snapshot = new
        {
            more = new ScanRowsPage([row], new ScanCursor(RenamerFileKind.Video, 9), 1, false),
            budgetExhausted = new ScanRowsPage([], new ScanCursor(RenamerFileKind.Audio, 500), 500, true),
            end = new ScanRowsPage([row], null, 1, false),
        };
        AssertSnapshot("scan-rows-page", JsonSerializer.Serialize(snapshot, PreviewResponse_));
    }

    [Fact]
    public void SummaryAndPickerDtos_CamelCase()
    {
        var snapshot = new
        {
            lastBatch = new LastBatchSummary(HasBatch: true, Count: 3, WrittenAtUtcTicks: 638000000000000000L, Consumed: false),
            undoResult = new UndoResult(
                Undone: 2,
                Failed: [new UndoEntryError(7, "/new/a.mkv", "/old/a.mkv", "locked")],
                Skipped: []),
            previewSample = new PreviewSampleResult(
                SampleLabel: "Video", OldName: "raw.mkv", NewName: "Title.mkv",
                Folder: "Studio/2021", Flags: ["sanitized"], DroppedFields: []),
        };
        AssertSnapshot("summary-picker-dtos", JsonSerializer.Serialize(snapshot, PreviewResponse_));
    }

    private static void AssertSnapshot(string name, string actualCompactJson, [CallerFilePath] string? callerPath = null)
    {
        using var doc = JsonDocument.Parse(actualCompactJson);
        string actual = JsonSerializer.Serialize(doc.RootElement, Indented);

        var dir = Path.Combine(Path.GetDirectoryName(callerPath)!, "fixtures");
        var path = Path.Combine(dir, name + ".json");

        if (Environment.GetEnvironmentVariable("WIRE_SNAPSHOT_UPDATE") == "1")
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, actual);
            return;
        }

        Assert.True(File.Exists(path), $"Missing wire-snapshot fixture: {path} (run with WIRE_SNAPSHOT_UPDATE=1 to create).");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));
    }
}
