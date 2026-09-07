using WhisparrSync.Client;
using WhisparrSync.Contracts;
using WhisparrSync.Discovery;

namespace WhisparrSync.Tests.Caching;

/// <summary>
/// The single-slot memoizer + the discovery cache-key composition, host-free. Proves the TtlCache hit/miss
/// contract (an identical key is a hit, a different key re-fetches, a failed fetch is never cached) and that the
/// discovery key includes the connected version + entity + page — so a version switch and a page change each
/// miss and read their own data rather than serving a stale prior slice.
/// </summary>
[Trait("Tier", "L0")]
public sealed class TtlCacheTests
{
    private sealed record Probe(string Tag);

    private static readonly TimeSpan LongTtl = TimeSpan.FromMinutes(5);

    // A fetch whose invocation count makes a HIT (no fetch) vs a MISS (a fetch) observable.
    private sealed class CountingFetch(string tag)
    {
        public int Calls { get; private set; }

        public Task<WhisparrResult<Probe>> RunAsync()
        {
            Calls++;
            return Task.FromResult(WhisparrResult<Probe>.Ok(new Probe(tag)));
        }
    }

    [Fact]
    public async Task An_identical_key_within_the_ttl_is_a_hit_with_no_second_fetch()
    {
        var cache = new TtlCache<Probe>(LongTtl);
        var fetch = new CountingFetch("a");

        var first = await cache.GetAsync("k", fetch.RunAsync, CancellationToken.None);
        var second = await cache.GetAsync("k", fetch.RunAsync, CancellationToken.None);

        Assert.True(first.IsOk);
        Assert.Equal(1, fetch.Calls);
        Assert.Same(first.Value, second.Value);
    }

    [Fact]
    public async Task A_different_key_misses_and_re_fetches()
    {
        var cache = new TtlCache<Probe>(LongTtl);
        var fetch = new CountingFetch("a");

        await cache.GetAsync("k1", fetch.RunAsync, CancellationToken.None);
        await cache.GetAsync("k2", fetch.RunAsync, CancellationToken.None);

        Assert.Equal(2, fetch.Calls);
    }

    [Fact]
    public async Task A_failed_fetch_is_not_cached_so_a_transient_outage_is_not_sticky()
    {
        var cache = new TtlCache<Probe>(LongTtl);
        var calls = 0;
        Task<WhisparrResult<Probe>> Failing()
        {
            calls++;
            return Task.FromResult(WhisparrResult<Probe>.Unreachable("down"));
        }

        var first = await cache.GetAsync("k", Failing, CancellationToken.None);
        var second = await cache.GetAsync("k", Failing, CancellationToken.None);

        Assert.False(first.IsOk);
        Assert.False(second.IsOk);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void The_discovery_key_composes_version_entity_and_page_distinctly()
    {
        var baseline = DiscoveryCacheKeys.For("v3", "https://box", "key", EntityKind.Studio, ["stash-1"], 1, DiscoveryQuery.Default);

        // Identical inputs → the identical key (a hit within the TTL).
        Assert.Equal(baseline, DiscoveryCacheKeys.For("v3", "https://box", "key", EntityKind.Studio, ["stash-1"], 1, DiscoveryQuery.Default));

        // A connected-version switch, a page change, a different entity, and a credential/box switch each yield a
        // distinct key — none of them can collide with the baseline slot.
        Assert.NotEqual(baseline, DiscoveryCacheKeys.For("v2", "https://box", "key", EntityKind.Studio, ["stash-1"], 1, DiscoveryQuery.Default));
        Assert.NotEqual(baseline, DiscoveryCacheKeys.For("v3", "https://box", "key", EntityKind.Studio, ["stash-1"], 2, DiscoveryQuery.Default));
        Assert.NotEqual(baseline, DiscoveryCacheKeys.For("v3", "https://box", "key", EntityKind.Performer, ["stash-1"], 1, DiscoveryQuery.Default));
        Assert.NotEqual(baseline, DiscoveryCacheKeys.For("v3", "https://box", "key", EntityKind.Studio, ["stash-2"], 1, DiscoveryQuery.Default));
        Assert.NotEqual(baseline, DiscoveryCacheKeys.For("v3", "https://box", "other", EntityKind.Studio, ["stash-1"], 1, DiscoveryQuery.Default));

        // A null page (the whole-catalogue re-derive) is its own slot, distinct from any paged read.
        Assert.NotEqual(baseline, DiscoveryCacheKeys.For("v3", "https://box", "key", EntityKind.Studio, ["stash-1"], page: null, DiscoveryQuery.Default));
    }

    [Fact]
    public async Task A_version_switch_misses_and_serves_its_own_slice_not_the_prior_version()
    {
        var cache = new TtlCache<Probe>(LongTtl);
        var calls = 0;
        Task<WhisparrResult<Probe>> Fetch(string tag)
        {
            calls++;
            return Task.FromResult(WhisparrResult<Probe>.Ok(new Probe(tag)));
        }

        var v3Key = DiscoveryCacheKeys.For("v3", "https://box", "key", EntityKind.Studio, ["stash-1"], 1, DiscoveryQuery.Default);
        var v2Key = DiscoveryCacheKeys.For("v2", "https://box", "key", EntityKind.Studio, ["stash-1"], 1, DiscoveryQuery.Default);

        var v3 = await cache.GetAsync(v3Key, () => Fetch("v3-slice"), CancellationToken.None);
        var v2 = await cache.GetAsync(v2Key, () => Fetch("v2-slice"), CancellationToken.None);

        // The v2 read did not serve the v3-cached slice — it missed on the version and fetched its own.
        Assert.Equal("v3-slice", v3.Value!.Tag);
        Assert.Equal("v2-slice", v2.Value!.Tag);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Page_two_misses_and_serves_its_own_slice_not_page_ones()
    {
        var cache = new TtlCache<Probe>(LongTtl);
        var calls = 0;
        Task<WhisparrResult<Probe>> Fetch(string tag)
        {
            calls++;
            return Task.FromResult(WhisparrResult<Probe>.Ok(new Probe(tag)));
        }

        var page1Key = DiscoveryCacheKeys.For("v3", "https://box", "key", EntityKind.Studio, ["stash-1"], 1, DiscoveryQuery.Default);
        var page2Key = DiscoveryCacheKeys.For("v3", "https://box", "key", EntityKind.Studio, ["stash-1"], 2, DiscoveryQuery.Default);

        var page1 = await cache.GetAsync(page1Key, () => Fetch("page-1"), CancellationToken.None);
        var page2 = await cache.GetAsync(page2Key, () => Fetch("page-2"), CancellationToken.None);

        // Page 2 never served page 1's cached slice — its distinct key drove its own read.
        Assert.Equal("page-1", page1.Value!.Tag);
        Assert.Equal("page-2", page2.Value!.Tag);
        Assert.Equal(2, calls);
    }
}
