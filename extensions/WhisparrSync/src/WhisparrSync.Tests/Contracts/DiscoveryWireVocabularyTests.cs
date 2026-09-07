using System.Text.Json;
using WhisparrSync.Contracts;
using static WhisparrSync.Contracts.WireSerializers;

namespace WhisparrSync.Tests.Contracts;

/// <summary>
/// The exact wire literal of the discovery missing-status vocabulary, plus the projection that carries it.
/// <see cref="DiscoveryMissingStatus"/> is a const-string class, not an enum — no converter is involved, and
/// serializing the class itself proves nothing. The literals are asserted directly, and a
/// <see cref="MissingScene"/> carrying one is serialized through the response options the discovery endpoint
/// ships on. A casing drift blanks a frontend label and raises no error.
/// Bare-safe (DTOs + serializer statics live on the always-referenced product assembly).
/// </summary>
[Trait("Tier", "L0")]
public sealed class DiscoveryWireVocabularyTests
{
    private static readonly JsonSerializerOptions RequestBinding = new(JsonSerializerDefaults.Web);

    private static MissingScene Scene(string status)
        => new(
            SourceId: "stash-a",
            Title: "A Scene",
            ReleaseDate: "2026-01-02",
            EntityName: "My Studio",
            PosterUrl: null,
            Status: status);

    [Fact]
    public void DiscoveryMissingStatus_carries_four_camelCase_literals()
    {
        Assert.Equal("notAdded", DiscoveryMissingStatus.NotAdded);
        Assert.Equal("wanted", DiscoveryMissingStatus.Wanted);
        Assert.Equal("unmonitored", DiscoveryMissingStatus.Unmonitored);
        Assert.Equal("unknown", DiscoveryMissingStatus.Unknown);
    }

    [Fact]
    public void MissingScene_carrying_the_abstention_emits_it_as_an_explicit_field()
    {
        // The abstention travels as a present field. An omitted one is read back as notAdded — by the record
        // default asserted below and by the client's own coalesce — which is the exact claim it withdraws.
        var wire = JsonSerializer.Serialize(
            Scene(DiscoveryMissingStatus.Unknown), EnumStringResponseJsonOptions);

        Assert.Contains("\"status\":\"unknown\"", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingScene_status_still_defaults_to_notAdded_for_an_older_payload()
    {
        var wire = JsonSerializer.Serialize(
            new MissingScene("stash-a", "A Scene", null, "My Studio", null), EnumStringResponseJsonOptions);

        Assert.Contains("\"status\":\"notAdded\"", wire, StringComparison.Ordinal);
    }

    [Fact]
    public void Action_request_binds_a_body_with_and_without_a_page()
    {
        var withoutPage = JsonSerializer.Deserialize<DiscoveryActionRequest>(
            """{"CoveEntityId":5,"Kind":"studio","SourceId":"stash-a","Op":"search"}""", RequestBinding);
        var withPage = JsonSerializer.Deserialize<DiscoveryActionRequest>(
            """{"CoveEntityId":5,"Kind":"studio","SourceId":"stash-a","Op":"search","Page":3}""", RequestBinding);

        Assert.Null(withoutPage!.Page);
        Assert.Equal(3, withPage!.Page);
    }

    [Fact]
    public void ActionAll_request_binds_a_body_with_and_without_a_page()
    {
        var withoutPage = JsonSerializer.Deserialize<DiscoveryActionAllRequest>(
            """{"CoveEntityId":5,"Kind":"studio","Op":"monitor"}""", RequestBinding);
        var withPage = JsonSerializer.Deserialize<DiscoveryActionAllRequest>(
            """{"CoveEntityId":5,"Kind":"studio","Op":"monitor","SourceIds":["stash-a"],"Page":2}""",
            RequestBinding);

        Assert.Null(withoutPage!.Page);
        Assert.Null(withoutPage.SourceIds);
        Assert.Equal(2, withPage!.Page);
    }

    [Fact]
    public void A_body_naming_no_field_binds_to_all_nulls()
    {
        // Every field stays nullable, which is what routes a malformed body to the handler's own validation and
        // its clean 400 — never to a binding exception.
        var action = JsonSerializer.Deserialize<DiscoveryActionRequest>("{}", RequestBinding);

        Assert.NotNull(action);
        Assert.Null(action.CoveEntityId);
        Assert.Null(action.Kind);
        Assert.Null(action.SourceId);
        Assert.Null(action.Op);
        Assert.Null(action.Page);
    }
}
