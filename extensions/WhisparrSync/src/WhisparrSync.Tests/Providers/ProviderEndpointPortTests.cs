using Cove.Core.Interfaces;
using WhisparrSync.Contracts;
using WhisparrSync.Options;
using WhisparrSync.Providers;

namespace WhisparrSync.Tests.Providers;

/// <summary>
/// Which metadata server this product reads a catalogue from, and what it does when the host names
/// none.
/// </summary>
/// <remarks>
/// The configuration is optional throughout this extension, so every absence here has to reach an
/// answer rather than a throw: the extension must load on a host that registers no configuration at
/// all.
/// </remarks>
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

    /// <summary>A server carrying no credential is no server to read with.</summary>
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

    /// <summary>
    /// Two spellings of one source are one source, by the host's own rule. A match on the string
    /// alone would answer that a configured provider is absent.
    /// </summary>
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

    /// <summary>
    /// The identity endpoint is the CONFIGURED spelling, which is what the host stamps on a remote id
    /// row. Ownership is judged on it, so answering the standard address for a host configured at
    /// another would subtract against a spelling no row carries.
    /// </summary>
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
