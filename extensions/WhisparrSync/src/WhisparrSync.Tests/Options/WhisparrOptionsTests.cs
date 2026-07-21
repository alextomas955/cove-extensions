using System.Text.Json;
using WhisparrSync.Monitor;
using WhisparrSync.Options;

namespace WhisparrSync.Tests.Options;

/// <summary>
/// The executable contract for the version-aware identity configuration: the <see cref="WhisparrOptions.TpdbEndpoint"/>
/// round-trip (default + preserve-on-blank, mirroring <c>StashDbEndpoint</c>) and the
/// <see cref="WhisparrOptions.IdentityEndpoint"/> version selection (v3 → StashDB, v2 → ThePornDB) that keys
/// which of the entity's remote ids the server resolves as the Whisparr target.
/// </summary>
public sealed class WhisparrOptionsTests
{
    [Fact]
    public void TpdbEndpoint_DefaultsToThePornDbGraphql()
        => Assert.Equal("https://theporndb.net/graphql", new WhisparrOptions().TpdbEndpoint);

    [Fact]
    public void WithSubmitted_BlankTpdbEndpoint_PreservesStored()
    {
        var stored = new WhisparrOptions { TpdbEndpoint = "https://custom.tpdb/graphql" };

        var updated = stored.WithSubmitted(
            baseUrl: null, apiKey: null, selectedVersion: null, qualityProfileId: 0,
            tpdbEndpoint: "   ");

        Assert.Equal("https://custom.tpdb/graphql", updated.TpdbEndpoint);
    }

    [Fact]
    public void WithSubmitted_NonBlankTpdbEndpoint_Replaces()
    {
        var stored = new WhisparrOptions { TpdbEndpoint = "https://custom.tpdb/graphql" };

        var updated = stored.WithSubmitted(
            baseUrl: null, apiKey: null, selectedVersion: null, qualityProfileId: 0,
            tpdbEndpoint: "https://theporndb.net/graphql");

        Assert.Equal("https://theporndb.net/graphql", updated.TpdbEndpoint);
    }

    [Fact]
    public void OptionsView_CarriesTpdbEndpoint()
    {
        var view = OptionsView.From(new WhisparrOptions { TpdbEndpoint = "https://theporndb.net/graphql" });
        Assert.Equal("https://theporndb.net/graphql", view.TpdbEndpoint);
    }


    // The single server-side rule the outward endpoints key on: the identity endpoint follows the CONNECTED
    // version, so a v3 (Eros) instance resolves by the StashDB id and a v2 (Sonarr) instance by the TPDB id.

    [Theory]
    [InlineData("v3")]
    [InlineData("V3")]
    public void IdentityEndpoint_OnV3_IsStashDbEndpoint(string version)
    {
        var options = new WhisparrOptions
        {
            SelectedVersion = version,
            StashDbEndpoint = "https://stashdb.org/graphql",
            TpdbEndpoint = "https://theporndb.net/graphql",
        };

        Assert.Equal("https://stashdb.org/graphql", options.IdentityEndpoint);
    }

    [Theory]
    [InlineData("v2")]
    [InlineData("V2")]
    public void IdentityEndpoint_OnV2_IsTpdbEndpoint(string version)
    {
        var options = new WhisparrOptions
        {
            SelectedVersion = version,
            StashDbEndpoint = "https://stashdb.org/graphql",
            TpdbEndpoint = "https://theporndb.net/graphql",
        };

        Assert.Equal("https://theporndb.net/graphql", options.IdentityEndpoint);
    }

    // DefaultMonitorScope persists as its STRING name (the property-level converter), so a reorder of the
    // MonitorScope enum cannot silently repoint a stored blob to a different scope.

    [Fact]
    public void DefaultMonitorScope_SerializesAsStringName_AndRoundTrips()
    {
        var options = new WhisparrOptions { DefaultMonitorScope = MonitorScope.AllScenes };

        var json = JsonSerializer.Serialize(options, WhisparrOptions.JsonOptions);
        Assert.Contains("\"AllScenes\"", json);
        Assert.DoesNotContain("\"DefaultMonitorScope\":1", json); // never the numeric ordinal

        var roundTripped = JsonSerializer.Deserialize<WhisparrOptions>(json, WhisparrOptions.JsonOptions)!;
        Assert.Equal(MonitorScope.AllScenes, roundTripped.DefaultMonitorScope);
    }

    // Per-version connection memory: switching v3↔v2 in Settings restores that version's URL/key/root/profile,
    // so a user can toggle between a v3 and a v2 instance without re-entering either.

    [Fact]
    public void WithSubmitted_SavesConnectionPerVersion_AndSwitchingBackRestoresThatVersionKey()
    {
        // Configure v3, then v2 with its own key, then switch BACK to v3 with a BLANK key (the toggle-then-save
        // flow): the active key must restore to v3's saved key, never carry v2's.
        var v3 = new WhisparrOptions().WithSubmitted(
            baseUrl: "http://v3.local", apiKey: "V3-KEY", selectedVersion: "v3", qualityProfileId: 30);
        var v2 = v3.WithSubmitted(
            baseUrl: "http://v2.local", apiKey: "V2-KEY", selectedVersion: "v2", qualityProfileId: 20);

        Assert.Equal("http://v2.local", v2.BaseUrl);
        Assert.Equal("V2-KEY", v2.ApiKey);
        // v3's WHOLE connection (url + key + profile) survives the switch to v2 — this is what the UI reads
        // back to repopulate the form on a toggle, so assert every field, not just the key.
        var savedV3 = v2.SavedConnections["v3"];
        Assert.Equal("http://v3.local", savedV3.BaseUrl);
        Assert.Equal("V3-KEY", savedV3.ApiKey);
        Assert.Equal(30, savedV3.QualityProfileId);

        var backToV3 = v2.WithSubmitted(
            baseUrl: "http://v3.local", apiKey: "", selectedVersion: "v3", qualityProfileId: 30);

        Assert.Equal("http://v3.local", backToV3.BaseUrl);
        Assert.Equal("V3-KEY", backToV3.ApiKey);    // restored from the saved v3 connection, not blanked
        Assert.Equal("V2-KEY", backToV3.SavedConnections["v2"].ApiKey); // v2 still remembered for the next toggle
    }

    [Fact]
    public void WithSubmitted_BlankKeyOnNotYetConfiguredVersion_DoesNotInheritTheOtherVersionKey()
    {
        var v3 = new WhisparrOptions().WithSubmitted(
            baseUrl: "http://v3.local", apiKey: "V3-KEY", selectedVersion: "v3", qualityProfileId: 30);

        // First-ever switch to v2 with a blank key: there is nothing saved for v2, so the key resolves to empty —
        // NOT v3's key. This is the invariant that stops a toggle from leaking v3's key to the v2 host.
        var v2 = v3.WithSubmitted(
            baseUrl: "http://v2.local", apiKey: "", selectedVersion: "v2", qualityProfileId: 0);

        Assert.Equal("", v2.ApiKey);
    }

    [Fact]
    public void OptionsView_ExposesSavedConnections_FoldingInTheActiveVersion()
    {
        var options = new WhisparrOptions
        {
            BaseUrl = "http://v3.local",
            ApiKey = "V3-KEY",
            SelectedVersion = "v3",
            QualityProfileId = 30,
            SavedConnections = new Dictionary<string, WhisparrConnection>
            {
                ["v2"] = new("http://v2.local", "V2-KEY", 20),
            },
        };

        var view = OptionsView.From(options);

        Assert.True(view.SavedConnections["v2"].HasApiKey);
        Assert.Equal("http://v2.local", view.SavedConnections["v2"].BaseUrl);
        // The active v3 connection is folded in even though it predates the SavedConnections map (migration).
        Assert.True(view.SavedConnections["v3"].HasApiKey);
        Assert.Equal("http://v3.local", view.SavedConnections["v3"].BaseUrl);
    }

    // PathTranslation is a non-secret list setting modeled on the endpoint strings: WithSubmitted sets it, and
    // an absent (null) submission preserves the stored table so a partial save never wipes it. Default = empty.
    [Fact]
    public void WithSubmitted_PathTranslation_SetsThenPreservesOnNull()
    {
        Assert.Empty(new WhisparrOptions().PathTranslation); // default is identity (a shared mount)

        var updated = new WhisparrOptions().WithSubmitted(
            baseUrl: null, apiKey: null, selectedVersion: null, qualityProfileId: 0,
            pathTranslation: [new PathTranslationRule("/cove/library", "/data/media")]);
        var rule = Assert.Single(updated.PathTranslation);
        Assert.Equal("/cove/library", rule.CovePrefix);
        Assert.Equal("/data/media", rule.WhisparrPrefix);

        var unchanged = updated.WithSubmitted(
            baseUrl: null, apiKey: null, selectedVersion: null, qualityProfileId: 0,
            pathTranslation: null);
        Assert.Single(unchanged.PathTranslation); // null preserves the stored table
    }

    [Fact]
    public void WithSubmitted_DefaultMonitorScope_SetsThenPreservesOnNull()
    {
        Assert.Equal(MonitorScope.NewReleases, new WhisparrOptions().DefaultMonitorScope); // the loop-safe default

        var updated = new WhisparrOptions().WithSubmitted(
            baseUrl: null, apiKey: null, selectedVersion: null, qualityProfileId: 0,
            defaultMonitorScope: MonitorScope.AllScenes);
        Assert.Equal(MonitorScope.AllScenes, updated.DefaultMonitorScope);

        var unchanged = updated.WithSubmitted(
            baseUrl: null, apiKey: null, selectedVersion: null, qualityProfileId: 0,
            defaultMonitorScope: null);
        Assert.Equal(MonitorScope.AllScenes, unchanged.DefaultMonitorScope); // null preserves the stored value
    }
}
