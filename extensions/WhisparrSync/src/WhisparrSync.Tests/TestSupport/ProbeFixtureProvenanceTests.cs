using System.Text.Json;

namespace WhisparrSync.Tests.TestSupport;

// One place for the provider documents' provenance, so a case driving one of them asserts on what
// it answers rather than on where it came from.
public sealed class ProbeFixtureProvenanceTests
{
    [Theory]
    [InlineData("stashdb-2026-09-name-lookups.json", "https://stashdb.org/graphql")]
    [InlineData("stashdb-2026-09-scene-page.json", "https://stashdb.org/graphql")]
    [InlineData("stashdb-2026-09-facet-menus.json", "https://stashdb.org/graphql")]
    [InlineData("theporndb-2026-09-name-lookups.json", "https://api.theporndb.net")]
    [InlineData("theporndb-2026-09-openapi-version.json", "https://api.theporndb.net")]
    [InlineData("theporndb-2026-09-scenes-page.json", "https://api.theporndb.net")]
    [InlineData("theporndb-2026-09-scenes-at-ceiling.json", "https://api.theporndb.net")]
    [InlineData("theporndb-2026-09-scenes-short-first-page.json", "https://api.theporndb.net")]
    public void EveryProviderFixtureStatesWhenAndWhereItWasRecorded(string fixture, string against)
    {
        var document = JsonDocument.Parse(ProbeFixtures.Read(fixture)).RootElement;

        Assert.Equal("2026-09-06", document.GetProperty("recordedOn").GetString());
        Assert.StartsWith(
            against, document.GetProperty("recordedAgainst").GetString()!, StringComparison.Ordinal);
    }
}
