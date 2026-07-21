using System.Text.Json;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Options;

/// <summary>
/// Host-free contract for the options persistence + the redaction boundary: the store round-trips
/// the scalar config over a <see cref="FakeStore"/>, a corrupt/absent blob loads as safe defaults, the
/// <see cref="OptionsView"/> projection omits the raw API key (exposing only <c>hasApiKey</c>) and never
/// serializes the key value, and an empty submitted key preserves the stored one (write-only semantics).
/// </summary>
public sealed class OptionsRedactionTests
{
    // Collections (TagsOnAdd, SavedConnections) compare by reference under record value-equality, so a default
    // or round-tripped instance carries a distinct-instance empty collection. Normalize both to a shared empty
    // before a whole-record compare (element-wise assertions cover the collection contents where they matter).
    private static readonly IReadOnlyDictionary<string, WhisparrConnection> NoConnections =
        new Dictionary<string, WhisparrConnection>();

    private static WhisparrOptions NormalizeCollections(WhisparrOptions options)
        => options with { TagsOnAdd = [], SavedConnections = NoConnections, PathTranslation = [] };

    [Fact]
    public async Task LoadAsync_AbsentKey_ReturnsDefaults()
    {
        var store = new OptionsStore(new FakeStore());

        var loaded = await store.LoadAsync();

        Assert.Equal(NormalizeCollections(new WhisparrOptions()), NormalizeCollections(loaded)); // first run → defaults
    }

    [Fact]
    public async Task SaveAsync_ThenLoadAsync_RoundTripsScalarFields()
    {
        var store = new OptionsStore(new FakeStore());
        var custom = new WhisparrOptions
        {
            BaseUrl = "http://localhost:6969",
            ApiKey = "stored-secret",
            SelectedVersion = "v3",
            DetectedVersion = "3.3.4.808",
            QualityProfileId = 4,
            WebhookSecret = "wh-secret",
            TagsOnAdd = ["cove", "favorites"],
            MonitorNewByDefault = false,
            SearchOnAdd = true,
            AllowQualityUpgrades = false,
            SavedConnections = new Dictionary<string, WhisparrConnection>
            {
                ["v2"] = new("http://v2.local", "V2-KEY", 20),
            },
        };

        await store.SaveAsync(custom);
        var loaded = await store.LoadAsync();

        // Collections compare by reference under record value-equality, so assert them element-wise across the
        // JSON round-trip, then compare the remaining scalar fields with both sides' collections normalized.
        Assert.Equal(custom.TagsOnAdd, loaded.TagsOnAdd);
        Assert.Equal("http://v2.local", loaded.SavedConnections["v2"].BaseUrl);
        Assert.Equal("V2-KEY", loaded.SavedConnections["v2"].ApiKey); // the per-version key persists at rest
        Assert.Equal(NormalizeCollections(custom), NormalizeCollections(loaded));
    }

    [Fact]
    public async Task LoadAsync_CorruptBlob_ReturnsDefaults()
    {
        var fake = new FakeStore();
        await fake.SetAsync("options", "this is not json {{{");
        var store = new OptionsStore(fake);

        var loaded = await store.LoadAsync();

        Assert.Equal(NormalizeCollections(new WhisparrOptions()), NormalizeCollections(loaded)); // catches JsonException → defaults
    }

    [Fact]
    public void OptionsView_OmitsApiKey_ExposesHasApiKey()
    {
        // The projection type must have no ApiKey property at all — the raw key can never leave the server.
        Assert.Null(typeof(OptionsView).GetProperty("ApiKey"));

        var withKey = OptionsView.From(new WhisparrOptions { ApiKey = "super-secret-value-123", BaseUrl = "http://x" });
        var withoutKey = OptionsView.From(new WhisparrOptions { ApiKey = "", BaseUrl = "http://x" });
        Assert.True(withKey.HasApiKey);
        Assert.False(withoutKey.HasApiKey);

        // The serialized view exposes the hasApiKey boolean and never the key value.
        var json = JsonSerializer.Serialize(withKey);
        Assert.Contains("hasApiKey", json, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret-value-123", json, StringComparison.Ordinal);
    }

    [Fact]
    public void WithSubmitted_EmptyKey_PreservesStoredKey()
    {
        var stored = new WhisparrOptions { ApiKey = "stored-secret", BaseUrl = "http://old" };

        var updated = stored.WithSubmitted(
            baseUrl: "http://new", apiKey: "", selectedVersion: "v3", qualityProfileId: 2);

        Assert.Equal("stored-secret", updated.ApiKey); // empty submitted key ≠ clear the stored key
        Assert.Equal("http://new", updated.BaseUrl);
        Assert.Equal(2, updated.QualityProfileId);
    }

    [Fact]
    public void WithSubmitted_NonEmptyKey_ReplacesStoredKey()
    {
        var stored = new WhisparrOptions { ApiKey = "stored-secret" };

        var updated = stored.WithSubmitted(
            baseUrl: "http://x", apiKey: "new-secret", selectedVersion: "v3", qualityProfileId: 0);

        Assert.Equal("new-secret", updated.ApiKey);
    }
}
