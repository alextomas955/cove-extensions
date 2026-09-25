using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

// Asserted on the parsed body rather than on its text. A text assertion passes on a body carrying a
// member the instance discards, and this generation discards the other one's suppression spellings
// without saying so.
public sealed class SiteRegistrationBodyTests
{
    // This generation refuses a zero quality profile outright.
    private static readonly AddDefaults Defaults = new(1, "/config/library");

    // The site as v2's own lookup answered it, measured on 2.2.0.231.
    private const int SiteId = 5999;

    private const string NewItemRule = "monitorNewItems";

    private const string AddTimeCatalogue = "addOptions.monitor";

    private const string MonitoredFlag = "monitored";

    [Fact]
    public void ThePresenceOnlyBodyMonitorsNothingAtAll()
    {
        var body = Registered();

        Assert.False(Bool(body, MonitoredFlag));
        Assert.Equal("none", Text(body, NewItemRule));
        Assert.Equal("none", Text(body, AddTimeCatalogue));
    }

    // Presence is asserted apart from the value: an absent member and a false one read the same off
    // a value, and this generation's default for the absent case is not this product's to rely on.
    [Fact]
    public void ItSuppressesBothOfThisGenerationsSearches()
    {
        var body = Registered();

        Assert.Equal(2, ComposedAdds.V2Suppression.Length);
        Assert.All(
            ComposedAdds.V2Suppression,
            path =>
            {
                Assert.NotNull(ComposedAdds.At(body, path));
                Assert.False(Bool(body, path));
            });
    }

    // A body carrying one is composed for a schema other than the one it is sent to. The instance
    // discards it, so this product would be reading a suppression it never applied.
    [Fact]
    public void ItCarriesNoSuppressionSpellingThisGenerationDoesNotDeclare()
    {
        var body = Registered();

        Assert.All(
            ComposedAdds.EverySuppressionSpelling.Except(ComposedAdds.V2Suppression),
            path => Assert.Null(ComposedAdds.At(body, path)));
    }

    // The monitoring add sets the flag and monitors every later catalogue addition, which on a site
    // is a whole studio's worth of scenes. That is why the presence-only add is a member of its own.
    [Fact]
    public void TheMonitoringSiteAddStillWantsWhatThisOneDoesNot()
    {
        var monitoring = ComposedV2Body.Of(
            V2BodyProjector.AddStudio(
                SiteId, MonitorScope.AllScenes, Defaults));

        Assert.True(Bool(monitoring, MonitoredFlag));
        Assert.Equal("all", Text(monitoring, NewItemRule));
        Assert.Equal("all", Text(monitoring, AddTimeCatalogue));

        var presence = Registered();

        Assert.Equal(
            ["addOptions", NewItemRule, MonitoredFlag],
            monitoring
                .Where(member => !JsonNode.DeepEquals(member.Value, presence[member.Key]))
                .Select(member => member.Key)
                .Order(StringComparer.Ordinal));
    }

    private static JsonObject Registered()
        => ComposedV2Body.Of(V2BodyProjector.RegisterSite(SiteId, Defaults));

    private static bool Bool(JsonObject body, string path)
        => ComposedAdds.At(body, path)!.GetValue<bool>();

    private static string Text(JsonObject body, string path)
        => ComposedAdds.At(body, path)!.GetValue<string>();
}
