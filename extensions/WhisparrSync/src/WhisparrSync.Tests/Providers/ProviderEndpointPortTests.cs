using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Providers;

namespace WhisparrSync.Tests.Providers;

// The host configuration is optional, so the extension must load where none is registered.
public sealed class ProviderEndpointPortTests
{
    private const string ConfiguredSpelling = "https://stashdb.org/graphql";

    // Synthetic and authorises nothing: no request is sent anywhere in this file.
    private const string SomeKey = "0e2e0e2e0e2e0e2e0e2e0e2e0e2e0e2e";

    [Fact]
    public void AnAbsentConfigurationIsARefusalRatherThanAThrow()
    {
        var port = new ProviderEndpointPort(null);

        Assert.Null(port.Resolve(WhisparrGeneration.V3, new MetadataProviderEndpoints()));
    }

    [Fact]
    public void AConfigurationNamingNoMetadataServerIsARefusal()
    {
        var port = new ProviderEndpointPort(new CoveConfiguration());

        Assert.Null(port.Resolve(WhisparrGeneration.V3, new MetadataProviderEndpoints()));
    }

    [Fact]
    public void AMatchedServerWithNoKeyIsARefusal()
    {
        var port = PortOver((ConfiguredSpelling, "", 240));

        Assert.Null(port.Resolve(WhisparrGeneration.V3, new MetadataProviderEndpoints()));
    }

    [Fact]
    public void TheKeyAndTheRateComeFromTheMatchedEntry()
    {
        var port = PortOver(
            ("https://theporndb.net/graphql", "the-other-key", 60),
            (ConfiguredSpelling, SomeKey, 120));

        var resolved = port.Resolve(WhisparrGeneration.V3, new MetadataProviderEndpoints());

        Assert.NotNull(resolved);
        Assert.Equal(SomeKey, resolved.ApiKey);
        Assert.Equal(120, resolved.MaxRequestsPerMinute);
    }

    // The host treats these spellings as one source, so a plain string match would report a
    // configured provider as absent.
    [Theory]
    [InlineData("https://theporndb.net/graphql")]
    [InlineData("https://api.theporndb.net/graphql")]
    [InlineData("https://theporndb.net/graphql/")]
    public void TheTwoProviderSpellingsResolveToTheSameSource(string spelling)
    {
        var port = PortOver((spelling, SomeKey, 90));

        var resolved = port.Resolve(WhisparrGeneration.V2, new MetadataProviderEndpoints());

        Assert.NotNull(resolved);
        Assert.Equal(SomeKey, resolved.ApiKey);
        Assert.Equal(90, resolved.MaxRequestsPerMinute);
    }

    // The host stamps the configured spelling on a remote id row, and ownership is judged on it.
    // Answering the standard address instead would subtract against a spelling no row carries.
    [Fact]
    public void TheIdentityEndpointIsTheConfiguredSpellingRatherThanTheStandardOne()
    {
        const string configured = "https://api.theporndb.net/graphql";
        var port = PortOver((configured, SomeKey, 240));

        var resolved = port.Resolve(WhisparrGeneration.V2, new MetadataProviderEndpoints());

        Assert.NotNull(resolved);
        Assert.Equal(configured, resolved.IdentityEndpoint);
    }

    private static ProviderEndpointPort PortOver(
        params (string Endpoint, string ApiKey, int Rate)[] servers)
    {
        var config = new CoveConfiguration();
        foreach (var (endpoint, apiKey, rate) in servers)
        {
            config.Scraping.MetadataServers.Add(
                new MetadataServerInstance
                {
                    Endpoint = endpoint,
                    ApiKey = apiKey,
                    Name = endpoint,
                    MaxRequestsPerMinute = rate,
                });
        }

        return new ProviderEndpointPort(config);
    }
}
