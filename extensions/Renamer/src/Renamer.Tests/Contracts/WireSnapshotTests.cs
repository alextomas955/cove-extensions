using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Renamer.Contracts;
using Renamer.Planner;

namespace Renamer.Tests.Contracts;

/// <summary>
/// Pins the wire facts the committed OpenAPI document cannot state: the exact bytes the shared
/// serializer configuration produces, and the two response shapes whose loss a document diff would
/// pass over.
/// </summary>
/// <remarks>
/// Its expectations are transcribed by hand from real serialized bytes, so it is the one wire check
/// here that does not derive from the emitted document — the document, its schemas and the generated
/// TypeScript types all descend from a single source, and everything descending from a wrong source
/// agrees with the mistake. The failure it guards stays reachable: nothing stops the shared
/// configuration from losing its <see cref="JsonStringEnumConverter"/> or its naming policy, which
/// would leave the document and the generated types untouched while every enum on the wire moved.
/// <para>
/// A shape the document already carries is deliberately NOT snapshotted here. Continuous integration
/// re-emits the document and diffs it, so a schema change fails there; what a diff cannot fail on is a
/// serializer that stopped honouring its own configuration, or a value-level property — a field the
/// client derives being re-added, or a filter answering with nulls instead of zeros — that still
/// conforms to the schema it is described by.
/// </para>
/// <para>
/// What this class does not cover is WHICH options instance a handler hands its result.
/// <c>WireJsonResponseTests</c> reads the bytes a result actually writes, and that is the check for
/// this one's blind spot.
/// </para>
/// The response enums (<see cref="RenamerStatus"/> / <see cref="RenamerFileKind"/> /
/// <see cref="ConfirmLevel"/>) emit camelCase values; the persisted <c>RenamerOptions</c> blob keeps its
/// own PascalCase spelling independently. Set <c>WIRE_SNAPSHOT_UPDATE=1</c> to (re)write the fixtures.
/// Bare-safe.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class WireSnapshotTests
{
    // Web is test-local because it serializes the synthetic snapshot envelope below, which no product
    // response endpoint emits; the DTO responses ride an instance of the same configuration
    // PreviewResponseJsonOptions is built from.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions PreviewResponse_ = PreviewContracts.PreviewResponseJsonOptions;
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Every enum member's wire spelling in one table — the serializer-configuration contract itself.
    /// </summary>
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

    /// <summary>
    /// The scan row carries no field the client can derive and none no client reads.
    /// </summary>
    /// <remarks>
    /// This row multiplies by library size, so a field nobody reads is weight on every page — which is
    /// why the assertion is on ABSENCE. A schema listing the properties cannot state that rule: a
    /// re-added field is a schema addition, and an addition is the shape of change a document diff is
    /// most readily approved for.
    /// </remarks>
    [Fact]
    public void ScanRow_CarriesNoFieldTheClientCanDerive()
    {
        var item = new RenamerPlanItem(
            FileId: 42, OldFullPath: "/lib/a.mp3", NewFullPath: "/music/Artist - Song.mp3",
            Status: RenamerStatus.Move, NewBasename: "Artist - Song.mp3", TargetFolderPath: "/music",
            ResolvedDestinationRoot: "/music", MatchedRule: "Studio:7(direct)", TargetVolume: "/");
        // Every dropped field is POPULATED on the plan item above, so an absence below is the projection
        // dropping it rather than there being nothing to drop.
        string json = JsonSerializer.Serialize(
            ScanRow.From(RenamerFileKind.Audio, entityId: 5, item, inFlightPathOverflow: false),
            PreviewResponse_);

        foreach (var dropped in new[]
        {
            "resolvedDestinationRoot", "matchedRule", "targetVolume", "newBasename", "targetFolderPath",
        })
        {
            Assert.DoesNotContain(dropped, json, StringComparison.Ordinal);
        }

        // Non-empty control: an all-absent verdict over an empty document would pass for the wrong reason.
        Assert.Contains("\"newFullPath\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A caller who may read none of the scanned kinds is answered with zeros and an empty kind list,
    /// never with nulls or an error.
    /// </summary>
    /// <remarks>
    /// The merge is per-kind for a permission reason, so this is the arm a video-only reader hits
    /// against an image-only scan. Both answers conform to the same schema, so the document cannot tell
    /// them apart — and the panel renders a null total as an empty cell rather than as a failure.
    /// </remarks>
    [Fact]
    public void ScanSummaryView_AKindTheCallerMayNotRead_IsZeroedRatherThanNullOrThrowing()
    {
        var scanned = new ScanSummary(
            ScanSummary.CurrentSchemaVersion, 638000000000000000L,
            [
                new ScanKindSummary(
                    RenamerFileKind.Video, Entities: 3, Files: 3,
                    StatusCounts: [new ScanStatusCount(RenamerStatus.Rename, 3)],
                    BlastRadius: new PreviewSummary(
                        TotalCount: 3, SameVolumeCount: 0, CrossVolumeCount: 3, CrossVolumeBytes: 4096,
                        VolumePairs: [new VolumePairDelta("/src", "/dest", 3, 4096)],
                        ConfirmLevel: ConfirmLevel.Heavy, InFlightPathOverflowCount: 0),
                    VolumePairsTruncated: false),
            ]);

        var view = ScanSummaryView.From(scanned, [RenamerFileKind.Audio]);

        Assert.Empty(view.Kinds);
        Assert.Equal(0, view.TotalFiles);
        Assert.Equal(0, view.TotalEntities);
        Assert.Equal(0, view.WillChange);
        Assert.Equal(0, view.BlastRadius.TotalCount);
        Assert.Empty(view.BlastRadius.VolumePairs);
        // The scan still happened, so the timestamp is the one fact that survives the filter — without it
        // the panel would report a completed scan as never having run.
        Assert.Equal(638000000000000000L, view.CompletedAtUtcTicks);
    }

    /// <summary>
    /// A count beside a bounded sample, constructed so a shape that dropped the counts serializes
    /// differently here whatever the document says.
    /// </summary>
    [Fact]
    public void SummaryAndPickerDtos_CamelCase()
    {
        var snapshot = new
        {
            // A partially-restored batch, so the three counts in the fixture are distinguishable from
            // each other: 3 journalled, 1 already back, 1 gone for good, 1 still outstanding.
            lastBatch = new LastBatchSummary(
                HasBatch: true, Count: 3, RemainingCount: 1, UnrestorableCount: 1,
                WrittenAtUtcTicks: 638000000000000000L, Consumed: false),
            // A run whose failure total EXCEEDS its sample, so the fixture distinguishes a count from a
            // sample length: 9 saves threw, one of them is described. A shape that dropped the counts
            // and left only the arrays serializes differently here, whatever the document says.
            undoResult = new UndoResult(
                Undone: 2,
                FailedCount: 9,
                FailedSample: [new UndoEntryError(7, "/new/a.mkv", "/old/a.mkv", "locked")],
                SkippedCount: 0,
                SkippedSample: [],
                WarningCount: 1,
                WarningSample: [new UndoEntryWarning(7, "companion 'a.srt' stayed behind: target occupied")]),
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
