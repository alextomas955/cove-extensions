using System.Text.Json;
using WhisparrSync.Options;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests;

/// <summary>
/// Host-free contract for the options persistence + the CONN-06 redaction boundary: the store round-trips
/// the scalar config over a <see cref="FakeStore"/>, a corrupt/absent blob loads as safe defaults, the
/// <see cref="OptionsView"/> projection omits the raw API key (exposing only <c>hasApiKey</c>) and never
/// serializes the key value, and an empty submitted key preserves the stored one (write-only semantics).
/// </summary>
public sealed class OptionsRedactionTests
{
    [Fact]
    public async Task LoadAsync_AbsentKey_ReturnsDefaults()
    {
        var store = new OptionsStore(new FakeStore());

        var loaded = await store.LoadAsync();

        Assert.Equal(new WhisparrOptions(), loaded); // first run → defaults
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
            RootFolderId = 7,
            QualityProfileId = 4,
            WebhookSecret = "wh-secret",
        };

        await store.SaveAsync(custom);
        var loaded = await store.LoadAsync();

        Assert.Equal(custom, loaded);
    }

    [Fact]
    public async Task LoadAsync_CorruptBlob_ReturnsDefaults()
    {
        var fake = new FakeStore();
        await fake.SetAsync("options", "this is not json {{{");
        var store = new OptionsStore(fake);

        var loaded = await store.LoadAsync();

        Assert.Equal(new WhisparrOptions(), loaded); // catches JsonException → defaults
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
            baseUrl: "http://new", apiKey: "", selectedVersion: "v3", rootFolderId: 5, qualityProfileId: 2);

        Assert.Equal("stored-secret", updated.ApiKey); // empty submitted key ≠ clear the stored key
        Assert.Equal("http://new", updated.BaseUrl);
        Assert.Equal(5, updated.RootFolderId);
        Assert.Equal(2, updated.QualityProfileId);
    }

    [Fact]
    public void WithSubmitted_NonEmptyKey_ReplacesStoredKey()
    {
        var stored = new WhisparrOptions { ApiKey = "stored-secret" };

        var updated = stored.WithSubmitted(
            baseUrl: "http://x", apiKey: "new-secret", selectedVersion: "v3", rootFolderId: 0, qualityProfileId: 0);

        Assert.Equal("new-secret", updated.ApiKey);
    }
}
