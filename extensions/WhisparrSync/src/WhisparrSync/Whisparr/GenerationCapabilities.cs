using WhisparrSync.Contracts;

namespace WhisparrSync.Whisparr;

/// <summary>What each generation declares it can honour.</summary>
/// <remarks>
/// A declaration and nothing more. Which roles a connected instance actually has members for is the
/// interface set its type declares, and a caller reaches for a role by testing the instance for it.
/// These arrays are on the wire, so the settings page and the monitor menu can say what a
/// generation offers before any request is sent. One test asserts that each array names exactly the
/// roles its instance implements.
/// </remarks>
public static class GenerationCapabilities
{
    // Declaration order, so the list a browser reads is stable. A capability is listed only once
    // some generation has an implementation to register for it.
    //
    // No site-registration entry on v3: presence there is a scene add and a site arrives as a side
    // effect of one.
    //
    // Both generations hold the filesystem read. Each generated client declares the route, and v2
    // was measured answering it at 2.2.0.231, an unknown path giving an empty listing rather than a
    // failure.
    private static readonly WhisparrCapability[] V3Capabilities =
    [
        WhisparrCapability.OutOfBandCallbackSecret,
        WhisparrCapability.MonitorStudio,
        WhisparrCapability.MonitorPerformer,
        WhisparrCapability.RegisterMissingScenes,
        WhisparrCapability.ReflectOwnedFiles,
        WhisparrCapability.SearchMonitored,
        WhisparrCapability.ReadSceneStatus,
        WhisparrCapability.ReadSceneExclusions,
        WhisparrCapability.SearchScene,
        WhisparrCapability.MonitorScene,
        WhisparrCapability.ExcludeScene,
        WhisparrCapability.ReadEntityCardsInBatch,
        WhisparrCapability.ReadSceneCardsInBatch,
        WhisparrCapability.TrackEntityCatalogue,
        WhisparrCapability.ReadEntityCatalogue,
        WhisparrCapability.ReadInstanceFilesystem,
    ];

    // Measured against a real v2. It answers a not-found on every performer and per-scene route,
    // adds no catalogue item, and keeps no scene exclusions, so it holds none of those entries. It
    // does keep a row per scene, under a site and named by the provider's number, so the per-scene
    // monitor and the site-row read are held: what it lacks is a route reaching a scene without its
    // site, not the flag. Site registration and the held-site read are v2's alone, a site being its
    // unit of presence and its list the only route answering presence for many sites at once.
    private static readonly WhisparrCapability[] V2Capabilities =
    [
        WhisparrCapability.OutOfBandCallbackSecret,
        WhisparrCapability.MonitorStudio,
        WhisparrCapability.ReflectOwnedFiles,
        WhisparrCapability.SearchMonitored,
        WhisparrCapability.MonitorScene,
        WhisparrCapability.RegisterOwnedSites,
        WhisparrCapability.ReadSiteSceneRows,
        WhisparrCapability.ReadHeldSites,
        WhisparrCapability.ReadEntityCardsInBatch,
        WhisparrCapability.TrackEntityCatalogue,
        WhisparrCapability.ReadEntityCatalogue,
        WhisparrCapability.ReadInstanceFilesystem,
    ];

    // Whether widening the scope of something already monitored rewrites what that monitoring
    // already covers. v2 applies the scope it is given to the whole catalogue it holds, so a
    // narrower scope taken later withdraws the back catalogue again. v3 marks the existing scenes
    // wanted at the moment the wider scope is taken and leaves them wanted afterwards, so there the
    // wider scope is a one-way door.
    //
    // Not a capability: it describes how a route this product already calls behaves, not whether
    // the route is there. So it joins neither the enum nor either array.
    private static readonly Dictionary<WhisparrGeneration, bool> ScopeChangeIsRetroactive = new()
    {
        [WhisparrGeneration.V3] = false,
        [WhisparrGeneration.V2] = true,
    };

    // The authoritative list per generation. An unrecognised generation declares nothing.
    internal static IReadOnlyList<WhisparrCapability> CapabilitiesOf(WhisparrGeneration generation)
        => generation switch
        {
            WhisparrGeneration.V3 => V3Capabilities,
            WhisparrGeneration.V2 => V2Capabilities,
            _ => [],
        };

    // Throws on a generation that declared nothing, rather than answering one of the two: a false
    // here drops the warning a reader sees before a back catalogue is marked wanted, and a true
    // states that a scope taken back will undo it.
    internal static bool AScopeChangeIsRetroactiveOn(WhisparrGeneration generation)
        => ScopeChangeIsRetroactive[generation];

    internal static IReadOnlyCollection<WhisparrGeneration> GenerationsDeclaringScopeBehaviour
        => ScopeChangeIsRetroactive.Keys;
}
