using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Monitor;
using WhisparrSync.Options;
using WhisparrSync.SceneStatus;

namespace WhisparrSync.Tests.Contracts;

/// <summary>
/// Pins the server-side JSON wire of every response DTO + every enum, unified on all-camelCase.
/// Each value is serialized through the PRODUCT's OWN per-endpoint
/// <c>JsonSerializerOptions</c> statics (<c>WebResponseJsonOptions</c> and <c>EnumStringResponseJsonOptions</c>
/// in <c>Contracts/WireSerializers.cs</c>),
/// so a regression that drops the <see cref="JsonStringEnumConverter"/> or changes a naming policy — even one
/// that never touches a DTO attribute — flips a fixture red. These fixtures are frozen:
/// any churn must be a reviewed, deliberate wire change. Set <c>WIRE_SNAPSHOT_UPDATE=1</c> to (re)write the fixtures.
/// Bare-safe (DTOs + serializer statics live on the always-referenced product assembly).
/// </summary>
[Trait("Tier", "L0")]
public sealed class WireSnapshotTests
{
    // Only test-infra serializers are local: Web wraps the synthetic snapshot dictionaries (no product
    // endpoint emits those envelopes) and Indented is the pretty-printer. The DTO wire itself always rides
    // the product statics referenced through WireSerializers.*, so a snapshot can never pin a serializer the
    // product does not use.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public void Enums_WireStrings()
    {
        var snapshot = new
        {
            // SceneWhisparrState — camelCase via the type/property-level SceneWhisparrStateJsonConverter.
            sceneWhisparrState = new[]
                {
                    SceneWhisparrState.NotAdded, SceneWhisparrState.Excluded,
                    SceneWhisparrState.Unmonitored, SceneWhisparrState.Monitored,
                }
                .ToDictionary(v => v.ToString(), v => JsonSerializer.Serialize(v, WireSerializers.EnumStringResponseJsonOptions)),
        };
        AssertSnapshot("enums", JsonSerializer.Serialize(snapshot, Web));
    }

    [Fact]
    public void OptionsView_CamelCase_And_HasApiKey()
    {
        var options = new WhisparrOptions
        {
            BaseUrl = "http://whisparr:6969",
            ApiKey = "SECRET-KEY",
            SelectedVersion = "v3",
            DetectedVersion = "v3",
            // The probe-stamped verification tick rides the wire as a bare number, and a zero would ride as null
            // — the zero leg is pinned by ConnectionProjectionTests rather than by a second fixture here.
            DetectedVersionTicks = 638000000000000000L,
            TagsOnAdd = ["cove"],
            MonitorNewByDefault = true,
            AllowQualityUpgrades = true,
            TpdbEndpoint = "https://theporndb.net/graphql",
            WebhookHost = "http://cove:5000",
            SavedConnections = new Dictionary<string, WhisparrConnection>(StringComparer.OrdinalIgnoreCase)
            {
                ["v3"] = new("http://whisparr:6969", "SECRET-KEY"),
            },
        };
        AssertSnapshot("options-view", JsonSerializer.Serialize(OptionsView.From(options), WireSerializers.WebResponseJsonOptions));
    }

    /// <summary>
    /// Pins the <c>/status</c> projection's shape, including the unmet-required-option array the guarded controls
    /// consult before acting.
    /// </summary>
    /// <remarks>
    /// Serializes the handler's OWN <see cref="ConfigStatusResponse"/>, so the fixture pins the shipped shape
    /// rather than a mirror of it that could drift from the handler without failing. The array carries option keys
    /// only; a stored value appearing here would be a disclosure regression and would show up in this fixture.
    /// </remarks>
    [Fact]
    public void ConfigStatus_CamelCase_KeysOnly()
    {
        var incomplete = new WhisparrOptions { BaseUrl = "", ApiKey = "" };
        var complete = new WhisparrOptions
        {
            BaseUrl = "http://whisparr:6969",
            ApiKey = "SECRET-KEY",
            DetectedVersion = "3.3.4.808",
        };

        var snapshot = new
        {
            incomplete = StatusOf(incomplete),
            keyOnly = StatusOf(complete with { BaseUrl = "" }),
            complete = StatusOf(complete),
        };

        AssertSnapshot("config-status", JsonSerializer.Serialize(snapshot, WireSerializers.WebResponseJsonOptions));
    }

    // Mirrors StatusAsync's construction — the values come from the real guard, so a change to its keys or their
    // order flips the fixture.
    private static ConfigStatusResponse StatusOf(WhisparrOptions options)
    {
        var missing = ConfigCompletenessGuard.MissingRequiredOptions(options);
        return new ConfigStatusResponse(
            Configured: missing.Count == 0,
            HasApiKey: !string.IsNullOrEmpty(options.ApiKey),
            DetectedVersion: options.DetectedVersion,
            MissingRequiredOptions: missing);
    }

    [Fact]
    public void SceneCardStatus_Batch_CamelCase()
    {
        var batch = new Dictionary<string, SceneCardStatus>
        {
            ["101"] = new(SceneWhisparrState.Monitored, HasFile: true),
            ["102"] = new(SceneWhisparrState.NotAdded, HasFile: false),
        };
        AssertSnapshot("scene-card-status-batch", JsonSerializer.Serialize(batch, WireSerializers.EnumStringResponseJsonOptions));
    }

    [Fact]
    public void SceneDetail_CamelCaseState()
    {
        var detail = new SceneDetail(
            SceneWhisparrState.Unmonitored, Added: true, Monitored: false, HasFile: true,
            Quality: "WEB-DL 1080p", CutoffMet: false, ActionsSupported: true);
        AssertSnapshot("scene-detail", JsonSerializer.Serialize(detail, WireSerializers.EnumStringResponseJsonOptions));
    }

    [Fact]
    public void EntityStatus_And_MonitorResult_CamelCase()
    {
        var snapshot = new
        {
            monitorResult = new EntityMonitorResult(Added: true, Monitored: true),
            entityStatus = new EntityStatus(Added: true, Monitored: true, ScenesPresent: 3, ScenesTotal: 147),
        };
        AssertSnapshot("monitor", JsonSerializer.Serialize(snapshot, WireSerializers.EnumStringResponseJsonOptions));
    }

    [Fact]
    public void CamelCaseResponseDtos()
    {
        var snapshot = new
        {
            videosBatchResult = new VideosBatchResult("search", Total: 10, Succeeded: 7, Skipped: 2, Failed: 1),
            entitiesBatchResult = new EntitiesBatchResult("monitor", Total: 5, Succeeded: 4, Failed: 0, Skipped: 1),
            entityLibrarySummary = new EntityLibrarySummary(Total: 147, Monitored: 12),
            // An inbound provider model that is ALSO re-emitted verbatim as a Cove response — the
            // pass-through case check-wire-usage.mjs's response-DTO allowlist does not model, which is why
            // this side of the wire had no pin while the settings picker read it in the wrong casing. Both
            // halves of the row: a named profile and the null-name fallback the picker renders an id for.
            //
            // Pre-serialized through WebResponseJsonOptions — the static /qualityprofiles itself uses, which
            // is NOT the one wrapping this snapshot — and embedded as a parsed element so the wrapper cannot
            // restate the casing. Handed to the wrapper as a live object it would pin the wrong serializer.
            qualityProfile = ReSerialized(
                new[]
                {
                    new QualityProfile(Id: 1, Name: "Any"),
                    new QualityProfile(Id: 7, Name: null),
                },
                WireSerializers.WebResponseJsonOptions),
            syncPreview = new SyncPreviewResponse(
                new SyncPreviewCount(WithId: 100, Skipped: 47),
                new SyncPreviewCount(WithId: 0, Skipped: 0),
                new SyncPreviewCount(WithId: 0, Skipped: 0)),
            // importLogCounts dropped: the never-consumed /import-log counts field is
            // gone; the /import-log response is now { lastEventTicks, syncHealth }. syncHealth stays pinned.
            syncHealth = new SyncHealthView(PathMismatch: 2, LastMismatchTicks: 638000000000000000L, SamplePaths: ["/media/a.mp4"]),
            // The per-dependency health array, both legs. The first is a dependency that failed and RECOVERED —
            // a non-null healthy tick beside a non-null failure tick and a retained error is the sticky shape, so
            // a regression that cleared either on success shows up here as a null. The second is never-observed:
            // its ticks land as JSON null rather than 0, which is what stops a client rendering the epoch.
            pipelineHealth = new[]
            {
                new DependencyHealthView(
                    Dependency: "acquisition",
                    Outcome: "unreachable",
                    LastHealthyTicks: 638000000000000000L,
                    LastFailureTicks: 638000000000000001L,
                    ConsecutiveFailures: 3,
                    LastError: "Network is unreachable (host.docker.internal:6999)"),
                new DependencyHealthView(
                    Dependency: "metadata",
                    Outcome: "notConfigured",
                    LastHealthyTicks: null,
                    LastFailureTicks: null,
                    ConsecutiveFailures: 0,
                    LastError: ""),
            },
            // The webhook answer, both halves. The token-bearing url + registered describe the ACTIVE connection;
            // connections answers the same question per saved connection. The second entry is the one this pins:
            // an instance the user is NOT pointed at, holding no connector, and carrying no url property at all —
            // a url appearing on a per-connection entry would be a token-surface regression visible right here.
            webhookUrl = new WebhookUrlResponse(
                "http://cove:5000/api/extensions/x/webhook?token=abc",
                Registered: true,
                Connections:
                [
                    new WebhookConnectionView("v3", "http://whisparr:6969", Registered: true),
                    new WebhookConnectionView("v2", "http://whisparr-v2:6970", Registered: false),
                ]),
            // The folder-overlap answer, every leg of it. Two properties this entry exists to catch, both visible
            // in the recorded JSON: the response property derived from a C# keyword lands lowercase on the wire,
            // and every reason / finding-kind value lands as its camelCase literal. Those two vocabularies are
            // const-string classes precisely so no converter stands between the constant and the wire, so this
            // fixture is what proves that stayed true.
            folderOverlap = new
            {
                checkedAnswer = new FolderOverlapResponse(
                    Checked: true,
                    Reason: null,
                    Findings:
                    [
                        new RootContainmentFinding(
                            FolderFindingKind.RootContainment, "/data/media", "/data/media/scenes"),
                        new SceneFolderFindingView(
                            FolderFindingKind.SceneFolderFormat, "/data/media/scenes", "scenes", "/data/media"),
                    ],
                    NotApplicable: []),
                // A kind the connected generation cannot answer AT ALL is listed rather than reported clear.
                checkedWithAnUnanswerableKind = new FolderOverlapResponse(
                    Checked: true,
                    Reason: null,
                    Findings: [],
                    NotApplicable: [FolderFindingKind.SceneFolderFormat]),
                notChecked = new[]
                {
                    NotChecked(FolderOverlapReason.NotConfigured),
                    NotChecked(FolderOverlapReason.ReadFailed),
                    NotChecked(FolderOverlapReason.CoveRootsUnknown),
                    NotChecked(FolderOverlapReason.UnsupportedVersion),
                },
            },
        };
        // WebhookUrlResponse ships on WebResponseJsonOptions in the product. It carries no enum, so its bytes
        // are identical either way; the options-view fixture pins its served form.
        AssertSnapshot("camel-response-dtos", JsonSerializer.Serialize(snapshot, WireSerializers.EnumStringResponseJsonOptions));
    }

    // The handler's own abstention shape: the reason alone, with no finding array to mistake for "nothing found".
    private static FolderOverlapResponse NotChecked(string reason)
        => new(Checked: false, Reason: reason, Findings: [], NotApplicable: []);

    /// <summary>
    /// Serializes <paramref name="value"/> with <paramref name="options"/> and returns the result as a
    /// <see cref="JsonElement"/>, which an enclosing snapshot re-emits verbatim.
    /// </summary>
    /// <remarks>
    /// Lets one entry inside a snapshot ride a DIFFERENT product serializer than the one wrapping it, so an
    /// endpoint whose options static differs from its neighbours is pinned against its own.
    /// </remarks>
    private static JsonElement ReSerialized<T>(T value, JsonSerializerOptions options)
        => JsonDocument.Parse(JsonSerializer.Serialize(value, options)).RootElement.Clone();

    private static void AssertSnapshot(string name, string actualCompactJson, [CallerFilePath] string? callerPath = null)
    {
        // Re-indent for a reviewable multi-line diff WITHOUT changing any wire name/value: the property
        // names and enum strings are already baked into actualCompactJson; re-serializing the parsed
        // document only adds whitespace (order preserved).
        var snapshot = WireSnapshot.Resolve(actualCompactJson, name, callerPath!);
        if (snapshot.Wrote)
        {
            return;
        }

        Assert.True(
            snapshot.Exists,
            $"Missing wire-snapshot fixture: {snapshot.Path} (run with WIRE_SNAPSHOT_UPDATE=1 to create).");
        Assert.Equal(snapshot.Expected, snapshot.Actual);
    }
}
