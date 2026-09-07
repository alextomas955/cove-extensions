using WhisparrSync.Discovery;

namespace WhisparrSync.Tests.Discovery;

/// <summary>
/// The offline drift-lock for the pure credential resolver: Cove's configured metadata server
/// is the SOLE source (no extension-side key), matched on the registrable domain. The resolver and its
/// input/output records reference NO Cove type, so this test stays in the bare-CI compile leg. It is where the
/// Cove-config resolution is pinned, because the endpoint harness registers no <c>CoveConfiguration</c> to
/// exercise it end-to-end.
/// </summary>
[Trait("Tier", "L0")]
public sealed class MetadataCredentialResolverTests
{
    private const string StashDbTarget = "https://stashdb.org/graphql";

    [Fact]
    public void CoveConfig_matching_candidate_resolves_its_endpoint_key_and_rpm()
    {
        // A configured StashDB box with a key is the zero-setup path: the resolver carries that candidate's OWN
        // stored endpoint + key + rate limit — no extension key entered, Cove is the sole source.
        var candidates = new[]
        {
            new MetadataServerCandidate("https://theporndb.net/graphql", "tpdb-key", 120),
            new MetadataServerCandidate("https://stashdb.org/graphql", "cove-stashdb-key", 90),
        };

        var resolved = MetadataCredentialResolver.Resolve(candidates, StashDbTarget);

        Assert.NotNull(resolved);
        Assert.Equal("https://stashdb.org/graphql", resolved!.Endpoint);
        Assert.Equal("cove-stashdb-key", resolved.ApiKey);
        Assert.Equal(90, resolved.MaxRequestsPerMinute);
    }

    [Fact]
    public void No_matching_cove_candidate_resolves_to_null()
    {
        // No matching Cove box → null: the caller renders the actionable "set up a metadata source in Cove" state.
        var candidates = new[]
        {
            new MetadataServerCandidate("https://theporndb.net/graphql", "tpdb-key", 120),
        };

        Assert.Null(MetadataCredentialResolver.Resolve(candidates, StashDbTarget));
        Assert.Null(MetadataCredentialResolver.Resolve([], StashDbTarget));
    }

    [Theory]
    [InlineData("https://stashdb.org/graphql")]
    [InlineData("stashdb.org")]
    [InlineData("http://api.stashdb.org/graphql")]
    public void Registrable_domain_match_ignores_scheme_path_and_subhost(string candidateEndpoint)
    {
        // Reducing to the registrable domain (mirrors Cove's EndpointsMatch) is why a scheme/path/subhost
        // difference still resolves the same site.
        var candidates = new[] { new MetadataServerCandidate(candidateEndpoint, "cove-stashdb-key", 90) };

        var resolved = MetadataCredentialResolver.Resolve(candidates, StashDbTarget);

        Assert.NotNull(resolved);
        Assert.Equal(candidateEndpoint, resolved!.Endpoint);
    }

    [Fact]
    public void Different_registrable_domain_does_not_match()
    {
        // A theporndb.net box is NOT a StashDB credential: a different registrable domain must never satisfy a
        // stashdb.org target.
        var candidates = new[] { new MetadataServerCandidate("https://theporndb.net/graphql", "tpdb-key", 120) };

        Assert.Null(MetadataCredentialResolver.Resolve(candidates, StashDbTarget));
    }

    [Fact]
    public void Matching_domain_with_empty_key_is_not_a_match()
    {
        var candidates = new[] { new MetadataServerCandidate("https://stashdb.org/graphql", "", 90) };

        Assert.Null(MetadataCredentialResolver.Resolve(candidates, StashDbTarget));
    }
}
