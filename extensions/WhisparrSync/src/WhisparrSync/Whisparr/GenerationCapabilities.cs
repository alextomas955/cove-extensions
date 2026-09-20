using WhisparrSync.Contracts;
using WhisparrSync.Scene;

namespace WhisparrSync.Whisparr;

/// <summary>A capability the connected generation does not hold.</summary>
public sealed record CapabilityRefusal(WhisparrCapability Capability, WhisparrGeneration Generation);

/// <summary>A role obtained from a capability set, or the refusal standing in its place.</summary>
/// <remarks>
/// There is no third answer, and no way to reach the role without also stating what happens when it
/// is absent.
/// </remarks>
public sealed class Capability<TRole>
    where TRole : class
{
    private readonly TRole? _role;
    private readonly CapabilityRefusal? _refusal;

    internal Capability(TRole? role, CapabilityRefusal? refusal)
    {
        _role = role;
        _refusal = refusal;
    }

    /// <summary>
    /// Applies <paramref name="held"/> to the role, or <paramref name="refused"/> to the refusal.
    /// </summary>
    public TResult Match<TResult>(Func<TRole, TResult> held, Func<CapabilityRefusal, TResult> refused)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(refused);
        return _role is not null ? held(_role) : refused(_refusal!);
    }
}

/// <summary>The roles one Whisparr generation can honour.</summary>
/// <remarks>
/// A role a generation cannot honour is absent rather than present and refusing when it is called,
/// so a caller has no implementation to obtain and no way to express the request.
/// </remarks>
public sealed class WhisparrCapabilitySet
{
    // Every role declared here, whatever any generation holds: an absent role still needs a
    // capability to be refused under.
    private static readonly Dictionary<Type, WhisparrCapability> CapabilityByRole = new()
    {
        [typeof(IOutOfBandSecretRegistration)] = WhisparrCapability.OutOfBandCallbackSecret,
        [typeof(IWhisparrStudioActing)] = WhisparrCapability.MonitorStudio,
        [typeof(IWhisparrPerformerActing)] = WhisparrCapability.MonitorPerformer,
        [typeof(IWhisparrMissingSceneActing)] = WhisparrCapability.RegisterMissingScenes,
        [typeof(IWhisparrSiteRegistrationActing)] = WhisparrCapability.RegisterOwnedSites,
        [typeof(IWhisparrReflectOwnedActing)] = WhisparrCapability.ReflectOwnedFiles,
        [typeof(IWhisparrSearchGrabbing)] = WhisparrCapability.SearchMonitored,
        [typeof(IWhisparrSceneStatusReading)] = WhisparrCapability.ReadSceneStatus,
        [typeof(IWhisparrSceneExclusionReading)] = WhisparrCapability.ReadSceneExclusions,
        [typeof(IWhisparrSceneSearchGrabbing)] = WhisparrCapability.SearchScene,
        [typeof(IWhisparrSceneMonitorActing)] = WhisparrCapability.MonitorScene,
        [typeof(IWhisparrSceneExclusionActing)] = WhisparrCapability.ExcludeScene,
        [typeof(IWhisparrSiteSceneReading)] = WhisparrCapability.ReadSiteSceneRows,
        [typeof(IWhisparrHeldSiteReading)] = WhisparrCapability.ReadHeldSites,
        [typeof(IWhisparrInstanceFilesystemReading)] = WhisparrCapability.ReadInstanceFilesystem,
    };

    private readonly Dictionary<WhisparrCapability, object> _roles;

    internal WhisparrCapabilitySet(
        WhisparrGeneration generation, Dictionary<WhisparrCapability, object> roles)
    {
        Generation = generation;
        _roles = roles;

        // What the generation can honour, not what this set was built with: read off the
        // registrations, a set built for a read would report the generation as less capable.
        Held = GenerationCapabilities.CapabilitiesOf(generation);
    }

    /// <summary>The generation this set was built for.</summary>
    public WhisparrGeneration Generation { get; }

    /// <summary>The capabilities that generation holds.</summary>
    public IReadOnlyList<WhisparrCapability> Held { get; }

    /// <summary>
    /// The role <typeparamref name="TRole"/>, or the refusal standing in its place.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TRole"/> is not one of these roles, or it is one this generation holds
    /// and this set was built without the source that implements it. Neither is a generation gap, so
    /// neither is expressible as a refusal.
    /// </exception>
    public Capability<TRole> Obtain<TRole>()
        where TRole : class
    {
        if (!CapabilityByRole.TryGetValue(typeof(TRole), out var capability))
        {
            throw new InvalidOperationException(
                $"{typeof(TRole)} is not a Whisparr capability role. Add it to {nameof(CapabilityByRole)} "
                    + "beside the capability it expresses.");
        }

        if (_roles.TryGetValue(capability, out var role))
        {
            return new Capability<TRole>((TRole)role, null);
        }

        // A capability the generation holds, asked of a set built without the source implementing
        // it, is a construction fault. As a refusal it would read as a real generation gap.
        if (Held.Contains(capability))
        {
            throw new InvalidOperationException(
                $"{Generation} holds {capability}, but this capability set was built with no source for "
                    + $"{typeof(TRole)}. Build it through the overload taking a role set.");
        }

        return new Capability<TRole>(null, new CapabilityRefusal(capability, Generation));
    }
}

/// <summary>The capability set each generation is built with.</summary>
/// <remarks>
/// Built from the generation an instance reported rather than registered when the extension loads,
/// because which generation is connected is a stored setting rather than a compile-time fact.
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
        WhisparrCapability.ReadInstanceFilesystem,
    ];

    /// <summary>What <paramref name="generation"/> can honour, with no acting role supplied.</summary>
    /// <remarks>
    /// For a caller that only needs to know what the generation can do, or one of the capabilities
    /// needing no outbound client. Asking this set for an acting role throws rather than refusing,
    /// because a set built with no source for a capability the generation holds is a construction
    /// fault and not a generation gap.
    /// </remarks>
    public static WhisparrCapabilitySet For(WhisparrGeneration generation)
        => new(generation, RolesFor(generation, null));

    /// <summary>What <paramref name="generation"/> can honour, acting through <paramref name="roles"/>.</summary>
    internal static WhisparrCapabilitySet For(WhisparrGeneration generation, WhisparrRoleSet roles)
    {
        ArgumentNullException.ThrowIfNull(roles);
        return new WhisparrCapabilitySet(generation, RolesFor(generation, roles));
    }

    // The authoritative table, in declaration order. What a set was built with is a fact about the
    // caller, so reading capabilities off its registrations would answer differently per route.
    internal static IReadOnlyList<WhisparrCapability> CapabilitiesOf(WhisparrGeneration generation)
        => generation switch
        {
            WhisparrGeneration.V3 => V3Capabilities,
            WhisparrGeneration.V2 => V2Capabilities,
            _ => [],
        };

    // Both generations can carry a secret off the address, through fields they do not share: a
    // list-of-headers field on v3, a user-and-password pair on v2. The acting roles are registered
    // per generation and per entity kind, so a kind a generation cannot address is an absent
    // registration rather than a check inside a role.
    private static Dictionary<WhisparrCapability, object> RolesFor(
        WhisparrGeneration generation, WhisparrRoleSet? roles)
    {
        var registered = new Dictionary<WhisparrCapability, object>();
        switch (generation)
        {
            case WhisparrGeneration.V3:
                registered[WhisparrCapability.OutOfBandCallbackSecret] = new V3HeaderSecretRegistration();
                if (roles is not null)
                {
                    registered[WhisparrCapability.MonitorStudio] = roles.StudioActing;
                    registered[WhisparrCapability.MonitorPerformer] = roles.PerformerActing;
                    registered[WhisparrCapability.RegisterMissingScenes] = roles.MissingSceneActing;
                    registered[WhisparrCapability.ReflectOwnedFiles] = roles.ReflectOwnedActing;
                    registered[WhisparrCapability.SearchMonitored] = roles.SearchGrabbing;
                    registered[WhisparrCapability.ReadSceneStatus] = roles.SceneStatusReading;
                    registered[WhisparrCapability.ReadSceneExclusions] = roles.SceneExclusionReading;
                    registered[WhisparrCapability.SearchScene] = roles.SceneSearchGrabbing;
                    registered[WhisparrCapability.MonitorScene] = roles.SceneMonitorActing;
                    registered[WhisparrCapability.ExcludeScene] = roles.SceneExclusionActing;
                    registered[WhisparrCapability.ReadInstanceFilesystem] = roles.InstanceFilesystemReading;
                }

                break;

            // No performer registration: Whisparr v2 addresses no performer at all.
            case WhisparrGeneration.V2:
                registered[WhisparrCapability.OutOfBandCallbackSecret] = new V2BasicAuthSecretRegistration();
                if (roles is not null)
                {
                    registered[WhisparrCapability.MonitorStudio] = roles.StudioActing;
                    registered[WhisparrCapability.ReflectOwnedFiles] = roles.ReflectOwnedActing;
                    registered[WhisparrCapability.SearchMonitored] = roles.SearchGrabbing;
                    registered[WhisparrCapability.MonitorScene] = roles.SceneMonitorActing;
                    registered[WhisparrCapability.RegisterOwnedSites] = roles.SiteRegistrationActing;
                    registered[WhisparrCapability.ReadSiteSceneRows] = roles.SiteSceneReading;
                    registered[WhisparrCapability.ReadHeldSites] = roles.HeldSiteReading;
                    registered[WhisparrCapability.ReadInstanceFilesystem] = roles.InstanceFilesystemReading;
                }

                break;

            default:
                break;
        }

        return registered;
    }
}

// The role implementations one capability set acts through. Constructed only where a capability set
// is built and registered in no container, so no consumer holds a property bag handing out roles it
// never asked for. A role joins this record with its implementation, never ahead of it.
internal sealed record WhisparrRoleSet(
    IWhisparrStudioActing StudioActing,
    IWhisparrPerformerActing PerformerActing,
    IWhisparrMissingSceneActing MissingSceneActing,
    IWhisparrReflectOwnedActing ReflectOwnedActing,
    IWhisparrSearchGrabbing SearchGrabbing,
    IWhisparrSceneStatusReading SceneStatusReading,
    IWhisparrSceneExclusionReading SceneExclusionReading,
    IWhisparrSceneSearchGrabbing SceneSearchGrabbing,
    IWhisparrSceneMonitorActing SceneMonitorActing,
    IWhisparrSceneExclusionActing SceneExclusionActing,
    IWhisparrSiteRegistrationActing SiteRegistrationActing,
    IWhisparrSiteSceneReading SiteSceneReading,
    IWhisparrHeldSiteReading HeldSiteReading,
    IWhisparrInstanceFilesystemReading InstanceFilesystemReading)
{
    // The acting roles are implemented on the one type holding the HTTP client, so a client
    // implementing only some of them is a registration fault, not a capability a generation lacks.
    internal static WhisparrRoleSet From(IWhisparrClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client is IWhisparrStudioActing studioActing
            and IWhisparrPerformerActing performerActing
            and IWhisparrMissingSceneActing missingSceneActing
            and IWhisparrReflectOwnedActing reflectOwnedActing
            and IWhisparrSearchGrabbing searchGrabbing
            and IWhisparrSceneStatusReading sceneStatusReading
            and IWhisparrSceneExclusionReading sceneExclusionReading
            and IWhisparrSceneSearchGrabbing sceneSearchGrabbing
            and IWhisparrSceneMonitorActing sceneMonitorActing
            and IWhisparrSceneExclusionActing sceneExclusionActing
            and IWhisparrSiteRegistrationActing siteRegistrationActing
            and IWhisparrSiteSceneReading siteSceneReading
            and IWhisparrHeldSiteReading heldSiteReading
            and IWhisparrInstanceFilesystemReading instanceFilesystemReading
            ? new WhisparrRoleSet(
                studioActing,
                performerActing,
                missingSceneActing,
                reflectOwnedActing,
                searchGrabbing,
                sceneStatusReading,
                sceneExclusionReading,
                sceneSearchGrabbing,
                sceneMonitorActing,
                sceneExclusionActing,
                siteRegistrationActing,
                siteSceneReading,
                heldSiteReading,
                instanceFilesystemReading)
            : throw new InvalidOperationException(
                $"{client.GetType()} holds this product's HTTP client but implements only part of "
                    + $"{nameof(WhisparrRoleSet)}.");
    }
}
