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

    // v2 has no performer entry: it answers a not-found on every performer route and addresses a
    // performer only as a studio's catalogue. No missing-scene entry: no v2 route adds a catalogue
    // item, and its catalogue arrives only by re-reading its own metadata source. No scene-status,
    // scene-exclusion read, per-scene search or scene-exclusion write entry: v2 answers a not-found
    // on every per-scene route and keeps no scene exclusions.
    //
    // v2 holds the site-registration entry, which v3 does not: a site is v2's unit of presence.
    //
    // v2 holds the per-scene monitor entry and the site-row read: it does keep a row per scene,
    // under a site and named by the number the metadata provider issued. What it lacks is a
    // per-scene route addressing a scene without its site, not the monitored flag itself.
    //
    // v2 holds the held-site read, which v3 does not: v2 answers presence for a site through its own
    // list and by no other route, so many sites are one request there.
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
