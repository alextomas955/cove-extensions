using WhisparrSync.Discovery;

namespace WhisparrSync.Tests.Discovery;

/// <summary>
/// The pure routing table for a per-entity discovery read: one case per <see cref="DiscoveryRouter.Decide"/>
/// outcome, host-free. The catalogue source is the direct Cove-credential metadata provider, always — a resolved
/// credential routes <see cref="DiscoveryRoute.Direct"/>, no credential the actionable
/// <see cref="DiscoveryRoute.NeedsProviderKey"/>, and a missing id short-circuits to
/// <see cref="DiscoveryRoute.NoSourceId"/>. The internal enum keeps the cases as method-body locals (a public
/// [Theory] signature cannot carry an internal enum parameter).
/// </summary>
[Trait("Tier", "L0")]
public sealed class DiscoveryRouterTests
{
    private readonly record struct Case(bool HasRemoteId, bool HasDirectKey, DiscoveryRoute Expected);

    [Fact]
    public void Decide_routes_each_branch()
    {
        Case[] cases =
        [
            // A remote id resolves: a Cove metadata credential routes Direct, no credential is the actionable
            // needsProviderKey (never a thin fallback).
            new(true, true, DiscoveryRoute.Direct),
            new(true, false, DiscoveryRoute.NeedsProviderKey),
            // No remote id short-circuits regardless of the credential.
            new(false, true, DiscoveryRoute.NoSourceId),
            new(false, false, DiscoveryRoute.NoSourceId),
        ];

        foreach (var c in cases)
        {
            var route = DiscoveryRouter.Decide(c.HasRemoteId, c.HasDirectKey);
            Assert.Equal(c.Expected, route);
        }
    }
}
