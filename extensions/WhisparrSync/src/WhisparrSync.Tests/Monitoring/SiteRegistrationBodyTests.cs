using System.Text.Json.Nodes;
using WhisparrSync.Contracts;
using WhisparrSync.Monitoring;
using WhisparrSync.Tests.TestSupport;
using WhisparrSync.Whisparr;

namespace WhisparrSync.Tests.Monitoring;

/// <summary>The body the presence-only site add composes, as the instance would receive it.</summary>
/// <remarks>
/// Asserted on the parsed body rather than on its text. A text assertion passes on a body carrying a
/// member the instance discards, and this generation discards the other one's suppression spellings
/// without saying so.
/// <para>
/// The site add is a member of its own rather than a widening of the monitoring one, so what the two
/// bodies differ by is asserted here as well as what this one says.
/// </para>
/// </remarks>
public sealed class SiteRegistrationBodyTests
{
    /// <summary>A quality profile this generation accepts. It refuses a zero outright.</summary>
    private static readonly AddDefaults Defaults = new(1, "/config/library");

    /// <summary>The site as this generation's own lookup answered it, measured on 2.2.0.231.</summary>
    private const int SiteId = 5999;

    private const string SiteTitle = "Jay Bank Presents";

    private const string SiteSlug = "jay-bank-presents";

    /// <summary>The member this generation gates a later catalogue addition through.</summary>
    private const string NewItemRule = "monitorNewItems";

    /// <summary>The member this generation gates the add-time catalogue through.</summary>
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

    /// <summary>
    /// It carries both of this generation's acquisition-suppressing members, each present and false.
    /// </summary>
    /// <remarks>
    /// Presence is asserted apart from the value, because an absent member and a false one read the
    /// same off a value and this generation's own default for the absent case is not this product's
    /// to rely on.
    /// </remarks>
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

    /// <summary>It carries no suppression spelling belonging to the other generation.</summary>
    /// <remarks>
    /// A body carrying one is composed for a schema other than the one it is sent to: the instance
    /// discards it, so this product would be reading a suppression it never applied.
    /// </remarks>
    [Fact]
    public void ItCarriesNoSuppressionSpellingThisGenerationDoesNotDeclare()
    {
        var body = Registered();

        Assert.All(
            ComposedAdds.EverySuppressionSpelling.Except(ComposedAdds.V2Suppression),
            path => Assert.Null(ComposedAdds.At(body, path)));
    }

    /// <summary>
    /// The monitoring site add is untouched, and the two bodies differ only in what they monitor.
    /// </summary>
    /// <remarks>
    /// Why this is a member of its own. The monitoring add sets the flag and monitors every later
    /// catalogue addition, which on a site is a whole studio's worth of scenes.
    /// </remarks>
    [Fact]
    public void TheMonitoringSiteAddStillWantsWhatThisOneDoesNot()
    {
        var monitoring = ComposedV2Body.Of(
            V2BodyProjector.AddStudio(
                SiteId, SiteTitle, SiteSlug, MonitorScope.AllScenes, Defaults));

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
        => ComposedV2Body.Of(V2BodyProjector.RegisterSite(SiteId, SiteTitle, SiteSlug, Defaults));

    private static bool Bool(JsonObject body, string path)
        => ComposedAdds.At(body, path)!.GetValue<bool>();

    private static string Text(JsonObject body, string path)
        => ComposedAdds.At(body, path)!.GetValue<string>();
}
