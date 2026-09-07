using System.Text.Json;
using WhisparrSync.Adapters;
using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using Ext = global::WhisparrSync.WhisparrSync;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// The two connection-slice signals. Registration is answered per SAVED CONNECTION, not per
/// active connection — a connector that exists on an instance the user is not currently pointed at must not read
/// as unregistered — and a successful probe stamps the detected version only for the stored host.
/// </summary>
[Trait("Tier", "L0")]
public sealed class ConnectionProjectionTests
{
    private const string V3Url = "http://whisparr-v3:6971";
    private const string V2Url = "http://whisparr-v2:6972";

    private sealed class RecordingProbe(Dictionary<string, WhisparrResult<WebhookConnection?>> answers)
    {
        public List<(string Version, string BaseUrl, string ApiKey)> Calls { get; } = [];

        public Task<WhisparrResult<WebhookConnection?>> FindAsync(
            string version, string baseUrl, string apiKey, CancellationToken ct)
        {
            Calls.Add((version, baseUrl, apiKey));
            return Task.FromResult(
                answers.TryGetValue(version, out var answer) ? answer : WhisparrResult<WebhookConnection?>.Ok(null));
        }
    }

    private static WhisparrOptions BothVersionsSaved(string active = "v2") => new()
    {
        SelectedVersion = active,
        BaseUrl = active == "v2" ? V2Url : V3Url,
        ApiKey = active == "v2" ? "V2-KEY" : "V3-KEY",
        SavedConnections = new Dictionary<string, WhisparrConnection>(StringComparer.OrdinalIgnoreCase)
        {
            ["v3"] = new(V3Url, "V3-KEY"),
            ["v2"] = new(V2Url, "V2-KEY"),
        },
    };

    private static WebhookConnectionView Entry(IReadOnlyList<WebhookConnectionView> views, string version)
        => Assert.Single(views, v => v.Version == version);

    [Fact]
    public async Task AnswersPerSavedConnection_EachNamingItsOwnInstance()
    {
        var probe = new RecordingProbe(new()
        {
            // The connector lives on the instance the user is NOT currently pointed at.
            ["v3"] = WhisparrResult<WebhookConnection?>.Ok(new WebhookConnection(3, "http://cove:5073/hook?token=x")),
            ["v2"] = WhisparrResult<WebhookConnection?>.Ok(null),
        });

        var views = await Ext.WebhookConnectionsAsync(BothVersionsSaved(), probe.FindAsync, default);

        Assert.Equal(2, views.Count);
        Assert.True(Entry(views, "v3").Registered);
        Assert.False(Entry(views, "v2").Registered);
        Assert.Equal(V3Url, Entry(views, "v3").BaseUrl);
        Assert.Equal(V2Url, Entry(views, "v2").BaseUrl);
    }

    [Fact]
    public async Task EachEntryIsProbedWithItsOwnBaseUrlAndItsOwnKey()
    {
        var probe = new RecordingProbe([]);

        await Ext.WebhookConnectionsAsync(BothVersionsSaved(), probe.FindAsync, default);

        Assert.Equal(2, probe.Calls.Count);
        Assert.Contains(("v3", V3Url, "V3-KEY"), probe.Calls);
        Assert.Contains(("v2", V2Url, "V2-KEY"), probe.Calls);
        // The stored key of one generation is never presented to the other generation's host.
        Assert.DoesNotContain(probe.Calls, c => c.BaseUrl == V3Url && c.ApiKey == "V2-KEY");
        Assert.DoesNotContain(probe.Calls, c => c.BaseUrl == V2Url && c.ApiKey == "V3-KEY");
    }

    [Fact]
    public async Task ANonActiveEntryCarriesNoUrlValueAtAll()
    {
        var probe = new RecordingProbe(new()
        {
            ["v3"] = WhisparrResult<WebhookConnection?>.Ok(new WebhookConnection(3, "http://cove:5073/hook?token=SECRET")),
        });

        var views = await Ext.WebhookConnectionsAsync(BothVersionsSaved(), probe.FindAsync, default);

        // Structural, not incidental: the record declares no URL member, so no entry can ever carry the token.
        Assert.Null(typeof(WebhookConnectionView).GetProperty("Url"));
        var json = JsonSerializer.Serialize(views, WireSerializers.WebResponseJsonOptions);
        // The property key, not the substring: "baseUrl" legitimately ends in "url".
        Assert.DoesNotContain("\"url\":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadFailureDegradesThatEntryAloneAndDoesNotFailTheResponse()
    {
        var probe = new RecordingProbe(new()
        {
            ["v3"] = WhisparrResult<WebhookConnection?>.Unreachable("Connection refused"),
            ["v2"] = WhisparrResult<WebhookConnection?>.Ok(new WebhookConnection(9, "http://cove:5073/hook?token=x")),
        });

        var views = await Ext.WebhookConnectionsAsync(BothVersionsSaved(), probe.FindAsync, default);

        Assert.Equal(2, views.Count);
        Assert.False(Entry(views, "v3").Registered);
        Assert.True(Entry(views, "v2").Registered);
    }

    [Fact]
    public async Task AVersionWithNoSavedConnectionProducesNoEntry()
    {
        var options = new WhisparrOptions
        {
            SelectedVersion = "v3",
            BaseUrl = V3Url,
            ApiKey = "V3-KEY",
            SavedConnections = new Dictionary<string, WhisparrConnection>(StringComparer.OrdinalIgnoreCase)
            {
                ["v3"] = new(V3Url, "V3-KEY"),
            },
        };
        var probe = new RecordingProbe([]);

        var views = await Ext.WebhookConnectionsAsync(options, probe.FindAsync, default);

        Assert.Equal("v3", Assert.Single(views).Version);
        Assert.DoesNotContain(probe.Calls, c => c.Version == "v2");
    }

    // A record with every other field populated, so a persist that rewrote anything but the two version fields
    // is visible as a difference rather than as an absence.
    private static WhisparrOptions FullyPopulated() => new()
    {
        BaseUrl = V3Url,
        ApiKey = "V3-KEY",
        SelectedVersion = "v3",
        WebhookSecret = "WEBHOOK-SECRET",
        WebhookHost = "http://cove:5073",
        TagsOnAdd = ["cove", "eros"],
        SavedConnections = new Dictionary<string, WhisparrConnection>(StringComparer.OrdinalIgnoreCase)
        {
            // The active version's per-version entry deliberately holds a STALE key, which the top-level pair
            // does not. WithSubmitted resolves a blank key from this entry, so any rebuild through it — even one
            // faithfully re-supplying every current value — overwrites the active key and is caught below.
            ["v3"] = new(V3Url, "V3-KEY-STALE"),
            ["v2"] = new(V2Url, "V2-KEY"),
        },
    };

    [Fact]
    public async Task AProbeAgainstTheStoredHostStampsTheVersionAndItsTick_AndTouchesNothingElse()
    {
        var store = new OptionsStore(new FakeStore());
        var before = FullyPopulated();
        await store.SaveAsync(before, default);

        await Ext.PersistDetectedVersionAsync(store, V3Url, "3.3.4.808", 638_500_000_000_000_000L, default);

        var after = await store.LoadAsync();
        Assert.Equal("3.3.4.808", after.DetectedVersion);
        Assert.Equal(638_500_000_000_000_000L, after.DetectedVersionTicks);
        // The full-record load-modify-save is proven, not assumed.
        Assert.Equal(before.SavedConnections.Count, after.SavedConnections.Count);
        Assert.Equal(V2Url, after.SavedConnections["v2"].BaseUrl);
        Assert.Equal("V2-KEY", after.SavedConnections["v2"].ApiKey);
        Assert.Equal(before.TagsOnAdd, after.TagsOnAdd);
        Assert.Equal("WEBHOOK-SECRET", after.WebhookSecret);
        Assert.Equal("http://cove:5073", after.WebhookHost);
        Assert.Equal("V3-KEY", after.ApiKey);
    }

    [Fact]
    public async Task AProbeAgainstADifferentHostStampsNeitherTheVersionNorItsTick()
    {
        var store = new OptionsStore(new FakeStore());
        await store.SaveAsync(FullyPopulated() with { DetectedVersion = "3.0.0.1", DetectedVersionTicks = 7L }, default);

        await Ext.PersistDetectedVersionAsync(store, "http://attacker.example", "9.9.9", 638_500_000_000_000_000L, default);

        var after = await store.LoadAsync();
        Assert.Equal("3.0.0.1", after.DetectedVersion);
        Assert.Equal(7L, after.DetectedVersionTicks);
    }

    [Fact]
    public async Task TheStoredHostComparisonIgnoresATrailingSlashAndCase()
    {
        var store = new OptionsStore(new FakeStore());
        await store.SaveAsync(FullyPopulated(), default);

        await Ext.PersistDetectedVersionAsync(store, V3Url.ToUpperInvariant() + "/", "3.3.4.808", 42L, default);

        Assert.Equal("3.3.4.808", (await store.LoadAsync()).DetectedVersion);
    }

    [Fact]
    public void ANullTickIsProjectedForANeverDetectedVersion()
    {
        Assert.Null(OptionsView.From(FullyPopulated()).DetectedVersionTicks);
        Assert.Equal(42L, OptionsView.From(FullyPopulated() with { DetectedVersionTicks = 42L }).DetectedVersionTicks);
    }

    // A stamp that differs from both field defaults, so an assertion cannot pass by reading an unwritten field.
    private static WhisparrOptions Stamped()
        => FullyPopulated() with { DetectedVersion = "3.3.4.808", DetectedVersionTicks = 99L };

    [Fact]
    public void ASaveThatRepointsTheAddressDropsTheStamp()
    {
        // A save must never INVENT a reading — only a successful probe writes one. It must equally never carry one
        // across a repointing: the submission also changes the generation here, but the ADDRESS is the trigger, and
        // the two cases below separate them.
        var saved = Stamped().WithSubmitted(V2Url, "NEW-KEY", "v2");

        Assert.Equal("", saved.DetectedVersion);
        Assert.Equal(0L, saved.DetectedVersionTicks);
    }

    [Fact]
    public void ASaveThatRepointsTheAddressWithinAGenerationDropsTheStamp()
    {
        var saved = Stamped().WithSubmitted("http://whisparr-v3-other:6971", "", "v3");

        Assert.Equal("", saved.DetectedVersion);
        Assert.Equal(0L, saved.DetectedVersionTicks);
    }

    [Fact]
    public void AnAutoDetectedGenerationOnTheStoredAddressKeepsTheStamp()
    {
        // The reading is a fact about what the instance at this ADDRESS reported, so only an address change can
        // outlive it — moving the selector alone leaves it true.
        var saved = Stamped().WithSubmitted(V3Url, "", "v2");

        Assert.Equal("3.3.4.808", saved.DetectedVersion);
        Assert.Equal(99L, saved.DetectedVersionTicks);
    }

    [Fact]
    public void ASaveThatRepointsNothingKeepsTheStampAndItsTick()
    {
        var saved = Stamped().WithSubmitted(V3Url, "", "v3", tagsOnAdd: ["cove", "eros", "extra"]);

        Assert.Equal("3.3.4.808", saved.DetectedVersion);
        Assert.Equal(99L, saved.DetectedVersionTicks);
    }

    [Fact]
    public void TheInvalidationUsesTheSameHostRuleTheStampsOwnWriteGateUses()
    {
        var saved = Stamped().WithSubmitted(V3Url.ToUpperInvariant() + "/", "", "v3");

        Assert.Equal("3.3.4.808", saved.DetectedVersion);
        Assert.Equal(99L, saved.DetectedVersionTicks);
    }

    [Fact]
    public async Task TheActiveConnectionIsAnsweredBeforeItsFirstPerVersionSave()
    {
        // A blob that predates per-version storage: the active connection exists only at the top level.
        var options = new WhisparrOptions { SelectedVersion = "v3", BaseUrl = V3Url, ApiKey = "V3-KEY" };
        var probe = new RecordingProbe(new()
        {
            ["v3"] = WhisparrResult<WebhookConnection?>.Ok(new WebhookConnection(3, null)),
        });

        var views = await Ext.WebhookConnectionsAsync(options, probe.FindAsync, default);

        var only = Assert.Single(views);
        Assert.Equal("v3", only.Version);
        Assert.Equal(V3Url, only.BaseUrl);
        Assert.True(only.Registered);
    }
}
