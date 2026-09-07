using Cove.Core.Auth;
using Cove.Plugins;
using Microsoft.AspNetCore.Routing;
using WhisparrSync.Tests.TestSupport;

namespace WhisparrSync.Tests.Api;

/// <summary>
/// Security-critical: every extension route declares its access tier to the host, which enforces it in
/// middleware before the extension scope exists. A route that declares NOTHING is anonymous — Cove keeps that
/// for backward compatibility and only logs a warning — so a forgotten declaration is a silent hole, not a
/// build break. This is the guard that makes it loud.
/// </summary>
/// <remarks>
/// <para>
/// It reads the endpoints the extension REGISTERED rather than a hand-kept list, then cross-checks each one
/// against <see cref="ExpectedTiers"/>. Both directions fail: a route missing from the table (someone added a
/// route without stating its tier) and a table entry with no route (a stale expectation). That is why this
/// one test replaces the per-route deny matrices it succeeded — those could only cover routes someone
/// remembered to write a test for, and a new ungated route passed them by construction.
/// </para>
/// <para>
/// What it deliberately does NOT prove: that the host then denies the request. Cove's authorization
/// middleware lives in Cove.Api, which extensions do not reference, so enforcement is proven on the
/// containerized e2e leg and by live verification, not here.
/// </para>
/// </remarks>
[Trait("Tier", "L2")]
public sealed class RouteAccessTierTests
{
    private const string Base = "/api/extensions/com.alextomas955.whisparrsync";

    /// <summary>The tier a route is allowed to declare.</summary>
    private enum AccessTier
    {
        /// <summary>A side-effect-free projection: <c>extensions.read</c>.</summary>
        Read,

        /// <summary>Reaches the stored credentials or calls Whisparr: <c>extensions.configure</c>.</summary>
        Configure,

        /// <summary>Inbound from Whisparr, which holds no Cove principal: the shared secret is the auth.</summary>
        Token,
    }

    // The intended tier of every route, stated once. A new route MUST be added here, and the assertions below
    // then require it to carry the matching declaration in its registration.
    private static readonly Dictionary<string, AccessTier> ExpectedTiers = new(StringComparer.Ordinal)
    {
        ["POST /test-connection"] = AccessTier.Configure,
        ["GET /status"] = AccessTier.Read,
        ["GET /options"] = AccessTier.Read,
        ["POST /options"] = AccessTier.Configure,
        ["GET /webhook-url"] = AccessTier.Configure,
        ["POST /register-webhook"] = AccessTier.Configure,
        ["POST /webhook"] = AccessTier.Token,
        ["GET /import-log"] = AccessTier.Read,
        ["GET /folder-overlap"] = AccessTier.Configure,
        ["POST /monitor"] = AccessTier.Configure,
        ["POST /monitor-status"] = AccessTier.Configure,
        ["POST /entity-status-batch"] = AccessTier.Configure,
        ["GET /entity-library-summary"] = AccessTier.Configure,
        ["GET /scene-status-summary"] = AccessTier.Configure,
        ["POST /scene-status-batch"] = AccessTier.Configure,
        ["POST /scene-detail"] = AccessTier.Configure,
        ["POST /discovery/entity"] = AccessTier.Configure,
        ["GET /discovery/count"] = AccessTier.Configure,
        ["POST /discovery/action"] = AccessTier.Configure,
        ["POST /discovery/action-all"] = AccessTier.Configure,
        ["GET /activity/history"] = AccessTier.Configure,
        ["GET /activity/queue"] = AccessTier.Configure,
        ["GET /activity/wanted"] = AccessTier.Configure,
        ["POST /scene-add"] = AccessTier.Configure,
        ["POST /scene-search"] = AccessTier.Configure,
        ["POST /scene-monitor"] = AccessTier.Configure,
        ["POST /bulk-add-missing"] = AccessTier.Configure,
        ["POST /bulk-search-monitored"] = AccessTier.Configure,
        ["POST /reflect-owned"] = AccessTier.Configure,
        ["POST /scene-exclusion"] = AccessTier.Configure,
        ["POST /scene-grab-release"] = AccessTier.Configure,
        ["POST /scene-releases-list"] = AccessTier.Configure,
        ["POST /scene-search-upgrades"] = AccessTier.Configure,
        ["GET /file-settings"] = AccessTier.Configure,
        ["POST /file-settings"] = AccessTier.Configure,
        ["POST /videos-batch"] = AccessTier.Configure,
        ["POST /entities-batch"] = AccessTier.Configure,
        ["GET /sync-preview"] = AccessTier.Configure,
        ["POST /sync-library"] = AccessTier.Configure,
    };

    [Fact]
    public async Task EveryRegisteredRoute_DeclaresExactlyOneAccessTier()
    {
        var routes = await RegisteredRoutesAsync();

        // A harness that enumerated nothing would satisfy every per-route assertion below vacuously.
        Assert.NotEmpty(routes);

        var undeclared = routes
            .Where(route => TierOf(route) is null)
            .Select(ExtensionRouteHost.Describe)
            .ToList();
        Assert.Empty(undeclared);
    }

    [Fact]
    public async Task RegisteredRoutes_AndTheExpectedTierTable_AreTheSameSet()
    {
        var registered = (await RegisteredRoutesAsync()).Select(ShortName).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(registered.Except(ExpectedTiers.Keys, StringComparer.Ordinal));
        Assert.Empty(ExpectedTiers.Keys.Except(registered, StringComparer.Ordinal));
    }

    [Fact]
    public async Task EveryRoute_DeclaresTheTierItsCapabilityRequires()
    {
        var mismatched = new List<string>();
        foreach (var route in await RegisteredRoutesAsync())
        {
            var declared = TierOf(route);
            if (ExpectedTiers.TryGetValue(ShortName(route), out var expected) && declared != expected)
            {
                mismatched.Add($"{ExtensionRouteHost.Describe(route)}: declared {declared}, expected {expected}");
            }
        }

        Assert.Empty(mismatched);
    }

    [Fact]
    public async Task TheInboundWebhook_IsTheOnlyAnonymousRoute()
    {
        var anonymous = (await RegisteredRoutesAsync())
            .Where(route => route.Metadata.GetMetadata<CoveAllowAnonymousMetadata>() is not null)
            .Select(ShortName)
            .ToList();

        Assert.Equal(["POST /webhook"], anonymous);
    }

    [Fact]
    public async Task NoRoute_CombinesAPermissionTierWithAnEscapePolicy()
    {
        // The host rejects conflicting metadata at request time by denying the call, which would turn a
        // mis-declared route into a 403 nobody expected. Catch the contradiction here instead.
        var conflicting = (await RegisteredRoutesAsync())
            .Where(route =>
                (route.Metadata.GetMetadata<CoveAllowAnonymousMetadata>() is not null
                    || route.Metadata.GetMetadata<CoveAllowWithoutPermissionMetadata>() is not null)
                && route.Metadata.GetMetadata<CovePermissionRequirementMetadata>() is not null)
            .Select(ExtensionRouteHost.Describe)
            .ToList();

        Assert.Empty(conflicting);
    }

    private static async Task<IReadOnlyList<RouteEndpoint>> RegisteredRoutesAsync()
    {
        await using var host = await ExtensionRouteHost.BootAsync(CovePrincipal.Anonymous());
        return host.Routes;
    }

    private static AccessTier? TierOf(RouteEndpoint route)
    {
        if (route.Metadata.GetMetadata<CoveAllowAnonymousMetadata>() is not null)
        {
            return AccessTier.Token;
        }

        var permissions = route.Metadata.GetMetadata<CovePermissionRequirementMetadata>()?.Permissions;
        if (permissions is null)
        {
            return null;
        }

        if (permissions.Contains(Permissions.ExtensionsConfigure))
        {
            return AccessTier.Configure;
        }

        return permissions.Contains(Permissions.ExtensionsRead) ? AccessTier.Read : null;
    }

    private static string ShortName(RouteEndpoint route)
        => ExtensionRouteHost.Describe(route).Replace(Base, string.Empty, StringComparison.Ordinal);
}
