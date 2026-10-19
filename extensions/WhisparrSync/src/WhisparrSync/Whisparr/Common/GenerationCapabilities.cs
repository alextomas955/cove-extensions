using WhisparrSync.Contracts;
using WhisparrSync.Scene;

namespace WhisparrSync.Whisparr;

/// <summary>What each generation declares it can honour.</summary>
/// <remarks>
/// A declaration and nothing more. Which roles a connected instance actually has members for is the
/// interface set its type declares, and a caller reaches for a role by testing the instance for it.
/// These lists are on the wire, so the settings page and the monitor menu can say what a generation
/// offers before any request is sent. Each is read from the roles its own instance implements, so a
/// generation cannot offer a control with no member behind it, nor hold a member no control offers.
/// Why an instance holds the roles it does is stated on that instance.
/// </remarks>
public static class GenerationCapabilities
{
    // One row per capability, beside the role expressing it. This is the whole tie between the two:
    // a capability with no row here is declared by no generation.
    //
    // In the enum's own order, which is the order a generation's list reaches a browser in.
    private static readonly (WhisparrCapability Capability, Type Role)[] Roles =
    [
        (WhisparrCapability.OutOfBandCallbackSecret, typeof(IOutOfBandSecretRegistration)),
        (WhisparrCapability.MonitorStudio, typeof(IWhisparrStudioActing)),
        (WhisparrCapability.MonitorPerformer, typeof(IWhisparrPerformerActing)),
        (WhisparrCapability.RegisterMissingScenes, typeof(IWhisparrMissingSceneActing)),
        (WhisparrCapability.ReflectOwnedFiles, typeof(IWhisparrReflectOwnedActing)),
        (WhisparrCapability.SearchMonitored, typeof(IWhisparrSearchGrabbing)),
        (WhisparrCapability.ReadSceneStatus, typeof(IWhisparrSceneStatusReading)),
        (WhisparrCapability.ReadSceneExclusions, typeof(IWhisparrSceneExclusionReading)),
        (WhisparrCapability.SearchScene, typeof(IWhisparrSceneSearchGrabbing)),
        (WhisparrCapability.MonitorScene, typeof(IWhisparrSceneMonitorActing)),
        (WhisparrCapability.ExcludeScene, typeof(IWhisparrSceneExclusionActing)),
        (WhisparrCapability.RegisterOwnedSites, typeof(IWhisparrSiteRegistrationActing)),
        (WhisparrCapability.ReadSiteSceneRows, typeof(IWhisparrSiteSceneReading)),
        (WhisparrCapability.ReadHeldSites, typeof(IWhisparrHeldSiteReading)),
        (WhisparrCapability.ReadEntityCardsInBatch, typeof(IWhisparrEntityBatchReading)),
        (WhisparrCapability.ReadSceneCardsInBatch, typeof(IWhisparrSceneBatchReading)),
        (WhisparrCapability.TrackEntityCatalogue, typeof(IWhisparrEntityTrackingActing)),
        (WhisparrCapability.ReadEntityCatalogue, typeof(IWhisparrEntityCatalogueReading)),
        (WhisparrCapability.ReadInstanceFilesystem, typeof(IWhisparrInstanceFilesystemReading)),
    ];

    // Read once from the instance types, because which roles a type implements cannot change after
    // it is loaded. Declared below the rows it reads: a static initialiser runs where it is written.
    private static readonly Dictionary<WhisparrGeneration, IReadOnlyList<WhisparrCapability>>
        ByGeneration = new()
        {
            [WhisparrGeneration.V3] = DeclaredBy(typeof(WhisparrV3Instance)),
            [WhisparrGeneration.V2] = DeclaredBy(typeof(WhisparrV2Instance)),
        };

    // Whether widening the scope of something already monitored rewrites what that monitoring
    // already covers. v2 applies the scope it is given to the whole catalogue it holds, so a
    // narrower scope taken later withdraws the back catalogue again. v3 marks the existing scenes
    // wanted at the moment the wider scope is taken and leaves them wanted afterwards, so there the
    // wider scope is a one-way door.
    //
    // Not a capability: it describes how a route this product already calls behaves, not whether
    // the route is there. So it joins neither the enum nor the rows above.
    private static readonly Dictionary<WhisparrGeneration, bool> ScopeChangeIsRetroactive = new()
    {
        [WhisparrGeneration.V3] = false,
        [WhisparrGeneration.V2] = true,
    };

    // The authoritative list per generation. An unrecognised generation declares nothing.
    internal static IReadOnlyList<WhisparrCapability> CapabilitiesOf(WhisparrGeneration generation)
        => ByGeneration.TryGetValue(generation, out var declared) ? declared : [];

    // Throws on a generation that declared nothing, rather than answering one of the two: a false
    // here drops the warning a reader sees before a back catalogue is marked wanted, and a true
    // states that a scope taken back will undo it.
    internal static bool AScopeChangeIsRetroactiveOn(WhisparrGeneration generation)
        => ScopeChangeIsRetroactive[generation];

    internal static IReadOnlyCollection<WhisparrGeneration> GenerationsDeclaringScopeBehaviour
        => ScopeChangeIsRetroactive.Keys;

    private static IReadOnlyList<WhisparrCapability> DeclaredBy(Type instance)
    {
        var implemented = instance.GetInterfaces();
        return
        [
            .. Roles
                .Where(row => implemented.Contains(row.Role))
                .Select(row => row.Capability),
        ];
    }
}
