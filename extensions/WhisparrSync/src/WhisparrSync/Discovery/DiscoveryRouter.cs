namespace WhisparrSync.Discovery;

/// <summary>Which path serves a per-entity discovery read, and — for a non-serving outcome — the honest state it carries.</summary>
internal enum DiscoveryRoute
{
    /// <summary>No remote id resolved server-side → nothing to enumerate (the noSourceId state).</summary>
    NoSourceId,

    /// <summary>A remote id resolved but Cove has no matching metadata credential → the actionable needsProviderKey state.</summary>
    NeedsProviderKey,

    /// <summary>A remote id resolved and a Cove metadata credential resolved → read the catalogue directly (StashDB on v3, ThePornDB on v2 — cover + performer avatars).</summary>
    Direct,
}

/// <summary>
/// The server-side routing decision for a per-entity discovery read. Pure — no I/O — so the three-outcome
/// contract is unit-tested here and <c>ComputeDiscoveryAsync</c> only executes the decision. Given the two
/// server-resolved facts, it classifies the read into exactly one <see cref="DiscoveryRoute"/>. The state is
/// NEVER client-asserted: the caller renders whatever this decides.
/// </summary>
internal static class DiscoveryRouter
{
    /// <summary>
    /// Decides the route from the two resolved facts: whether a remote id resolved, and whether a Cove metadata
    /// credential resolved for the connected version's box (StashDB on v3, ThePornDB on v2).
    /// </summary>
    /// <remarks>
    /// The catalogue source is the direct Cove-credential metadata provider, always — monitored vs unmonitored no
    /// longer changes the source, and there is no Whisparr-presence read in routing. A missing id short-circuits
    /// to <see cref="DiscoveryRoute.NoSourceId"/> (nothing to read). With an id, a resolved credential routes
    /// <see cref="DiscoveryRoute.Direct"/> (the rich metadata source — cover + performer avatars); without one it
    /// is the actionable <see cref="DiscoveryRoute.NeedsProviderKey"/> (set up a metadata source in Cove), never a
    /// thin fallback.
    /// </remarks>
    public static DiscoveryRoute Decide(bool hasRemoteId, bool hasDirectKey)
    {
        if (!hasRemoteId)
        {
            return DiscoveryRoute.NoSourceId;
        }

        return hasDirectKey ? DiscoveryRoute.Direct : DiscoveryRoute.NeedsProviderKey;
    }
}
