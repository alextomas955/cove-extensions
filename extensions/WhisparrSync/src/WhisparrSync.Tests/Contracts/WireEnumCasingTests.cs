using System.Text.Json;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.SceneStatus;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync.Tests.Contracts;

/// <summary>
/// The exact string every wire-facing enum value serializes to, through the options static its endpoint ships
/// on. A casing change blanks a frontend label and raises no error. A round-trip assertion still passes on one.
/// </summary>
/// <remarks>
/// <see cref="MonitorScope"/> is the one enum that is not wire-facing at all — it persists PascalCase in the
/// options BLOB (an existing stored value must keep loading) and rides no response, so only that spelling is
/// pinned. Every wire-facing enum is camelCase.
/// </remarks>
[Trait("Tier", "L0")]
public sealed class WireEnumCasingTests
{
    private static string Wire(object value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(value, options);

    [Fact]
    public void SceneWhisparrState_is_camelCase_on_the_enum_carrying_options()
    {
        Assert.Equal("\"notAdded\"", Wire(SceneWhisparrState.NotAdded, EnumStringResponseJsonOptions));
        Assert.Equal("\"excluded\"", Wire(SceneWhisparrState.Excluded, EnumStringResponseJsonOptions));
        Assert.Equal("\"unmonitored\"", Wire(SceneWhisparrState.Unmonitored, EnumStringResponseJsonOptions));
        Assert.Equal("\"monitored\"", Wire(SceneWhisparrState.Monitored, EnumStringResponseJsonOptions));
    }

    [Fact]
    public void SceneWhisparrState_is_camelCase_even_on_the_plain_web_options()
    {
        // Its type-level converter, not the options, is what carries the casing — the property-level stamps this
        // baseline retired were never what held it.
        Assert.Equal("\"notAdded\"", Wire(SceneWhisparrState.NotAdded, WebResponseJsonOptions));
        Assert.Equal("\"monitored\"", Wire(SceneWhisparrState.Monitored, WebResponseJsonOptions));
    }

    [Fact]
    public void ActivityEnums_are_camelCase_on_the_enum_carrying_options()
    {
        Assert.Equal("\"grabbed\"", Wire(ActivityHistoryEvent.Grabbed, EnumStringResponseJsonOptions));
        Assert.Equal("\"imported\"", Wire(ActivityHistoryEvent.Imported, EnumStringResponseJsonOptions));
        Assert.Equal("\"failed\"", Wire(ActivityHistoryEvent.Failed, EnumStringResponseJsonOptions));

        Assert.Equal("\"downloading\"", Wire(ActivityQueueState.Downloading, EnumStringResponseJsonOptions));
        Assert.Equal("\"queued\"", Wire(ActivityQueueState.Queued, EnumStringResponseJsonOptions));
        Assert.Equal("\"importing\"", Wire(ActivityQueueState.Importing, EnumStringResponseJsonOptions));
        Assert.Equal("\"warning\"", Wire(ActivityQueueState.Warning, EnumStringResponseJsonOptions));
        Assert.Equal("\"failed\"", Wire(ActivityQueueState.Failed, EnumStringResponseJsonOptions));
    }

    [Fact]
    public void SceneCardStatus_carries_camelCase_state_on_the_static_the_batch_endpoint_ships_on()
    {
        Assert.Contains(
            "\"state\":\"notAdded\"",
            Wire(new SceneCardStatus(SceneWhisparrState.NotAdded, HasFile: false), EnumStringResponseJsonOptions),
            StringComparison.Ordinal);

        Assert.Contains(
            "\"state\":\"monitored\"",
            Wire(new SceneCardStatus(SceneWhisparrState.Monitored, HasFile: true), EnumStringResponseJsonOptions),
            StringComparison.Ordinal);
    }

    [Fact]
    public void HistoryRow_and_QueueRow_carry_camelCase_state_on_the_static_the_activity_endpoints_ship_on()
    {
        var scene = new ActivityScene("A Scene", null, null, null);

        Assert.Contains(
            "\"event\":\"imported\"",
            Wire(new HistoryRow(scene, ActivityHistoryEvent.Imported), EnumStringResponseJsonOptions),
            StringComparison.Ordinal);

        Assert.Contains(
            "\"state\":\"downloading\"",
            Wire(new QueueRow(scene, ActivityQueueState.Downloading, 42, "10m"), EnumStringResponseJsonOptions),
            StringComparison.Ordinal);
    }

    [Fact]
    public void MonitorScope_persists_PascalCase_in_the_options_blob()
    {
        // The blob spelling is a STORED value: changing it would fail to load an existing options blob. It rides
        // no response, so the blob is the only casing this enum has to keep.
        var blob = JsonSerializer.Serialize(
            new WhisparrOptions { DefaultMonitorScope = MonitorScope.AllScenes });
        Assert.Contains("\"AllScenes\"", blob, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_survivors_emit_camelCase_property_names()
    {
        Assert.Equal("{\"a\":1}", Wire(new { A = 1 }, WebResponseJsonOptions));
        Assert.Equal("{\"a\":1}", Wire(new { A = 1 }, EnumStringResponseJsonOptions));
    }
}
