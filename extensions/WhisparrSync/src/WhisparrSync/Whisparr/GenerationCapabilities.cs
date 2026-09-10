using WhisparrSync.Contracts;
using WhisparrSync.Scene;

namespace WhisparrSync.Whisparr;

/// <summary>A capability the connected generation does not hold.</summary>
/// <param name="Capability">The capability that was asked for.</param>
/// <param name="Generation">The generation it was refused on.</param>
public sealed record CapabilityRefusal(WhisparrCapability Capability, WhisparrGeneration Generation);

/// <summary>A role obtained from a capability set, or the refusal standing in its place.</summary>
/// <remarks>
/// There is no third answer, and no way to reach the role without also stating what happens when it
/// is absent.
/// </remarks>
/// <typeparam name="TRole">The role asked for.</typeparam>
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
    // Every role this product declares, whatever any generation holds: an absent role still needs a
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
    };

    private readonly Dictionary<WhisparrCapability, object> _roles;

    internal WhisparrCapabilitySet(
        WhisparrGeneration generation, Dictionary<WhisparrCapability, object> roles)
    {
        Generation = generation;
        _roles = roles;

        // What the generation can honour, not what this set happened to be built with. Read off the
        // registrations it would report whichever roles a caller supplied, so a set built for a read
        // would tell a browser the generation cannot do what it can.
        Held = GenerationCapabilities.CapabilitiesOf(generation);
    }

    /// <summary>The generation this set was built for.</summary>
    public WhisparrGeneration Generation { get; }

    /// <summary>The capabilities that generation holds.</summary>
    public IReadOnlyList<WhisparrCapability> Held { get; }

    /// <summary>
    /// The role <typeparamref name="TRole"/>, or the refusal standing in its place.
    /// </summary>
    /// <typeparam name="TRole">The role asked for.</typeparam>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TRole"/> is not one of this product's roles, or it is one this generation
    /// holds and this set was built without the source that implements it. Neither says anything
    /// about a generation, so neither is expressible as a refusal.
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

        // A capability the generation HOLDS, asked of a set built without the source implementing it,
        // is a construction fault. Answered as a refusal it would be indistinguishable from a real
        // generation gap, which is the silent-bug class the capability split exists to remove.
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
    // Declaration order, so the list a browser reads is stable. A capability is written down here
    // only once some generation has an implementation to register for it: registered ahead of one it
    // would report a capability whose only possible answer is a fault.
    //
    // No site-registration entry here: on this generation presence is a scene add and a site arrives
    // as a side effect of one, so it has no implementation to register.
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
    ];

    /// <inheritdoc cref="V3Capabilities"/>
    /// <remarks>
    /// No performer entry: this generation answers a not-found on every performer route and addresses
    /// one only as a studio's own catalogue, so a caller obtains no role and has to state what happens
    /// instead. No missing-scene entry either: no route on this generation adds a catalogue item at
    /// all, and its catalogue arrives only by re-reading its own metadata source. No scene-status
    /// entry: this generation answers a not-found on every per-scene route, so no retry can establish
    /// a status and a card states that rather than offering a gesture that cannot change it. No
    /// scene-exclusion entry for the same reason: a generation keeping no scene records keeps no
    /// scene exclusions, and this generation answers a not-found on the exclusion route too. No
    /// per-scene search entry either, for the same reason: a generation holding no scene has none to
    /// be searched for, and the entity search it does hold covers everything the entity monitors. No
    /// scene-exclusion write entry, because it keeps no scene exclusions to write to. It does hold
    /// a site-registration entry, which the newer generation does not: here a site is the unit of
    /// presence, and there a site arrives as a side effect of a scene add.
    /// <para>
    /// It also holds the per-scene monitor entry and the site-row read. This generation does hold a
    /// row per scene, under a site and named by the number the metadata provider issued, so both
    /// have an implementation to register. Measured on 2026-09-10 against
    /// <c>whisparr:v2-2.2.0-release.231</c>: for the site whose own number is 5999, 412 of 412 rows
    /// carried a number equal to a ThePornDB <c>_id</c> for that site, and the route setting the
    /// flag on named rows answered 202 and left exactly those rows monitored. What is absent is a
    /// per-scene route addressing a scene without its site, not the flag itself.
    /// </para>
    /// </remarks>
    private static readonly WhisparrCapability[] V2Capabilities =
    [
        WhisparrCapability.OutOfBandCallbackSecret,
        WhisparrCapability.MonitorStudio,
        WhisparrCapability.ReflectOwnedFiles,
        WhisparrCapability.SearchMonitored,
        WhisparrCapability.MonitorScene,
        WhisparrCapability.RegisterOwnedSites,
        WhisparrCapability.ReadSiteSceneRows,
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

    /// <summary>The capabilities <paramref name="generation"/> can honour, in declaration order.</summary>
    /// <remarks>
    /// The authoritative table. What a set was BUILT with is a fact about the caller, so reading a
    /// generation's capabilities off its registrations would answer differently depending on which
    /// route asked.
    /// </remarks>
    internal static IReadOnlyList<WhisparrCapability> CapabilitiesOf(WhisparrGeneration generation)
        => generation switch
        {
            WhisparrGeneration.V3 => V3Capabilities,
            WhisparrGeneration.V2 => V2Capabilities,
            _ => [],
        };

    // Both generations can carry a secret off the address, by fields neither shares with the other:
    // a list-of-headers field on one, a user-and-password pair on the other. The implementation is
    // therefore per generation, and a generation whose schema declared neither would hold no role at
    // all rather than one that refused once it was called.
    //
    // The acting roles are registered per generation AND per entity kind for the same reason: the
    // kind one generation cannot address at all is an absent registration rather than a check inside
    // a role that claims to cover it.
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
                }

                break;

            // No performer registration in either table. The older generation addresses no performer
            // at all, so a caller obtains no role and has to state what happens instead.
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
                }

                break;

            default:
                break;
        }

        return registered;
    }
}

/// <summary>The role implementations one capability set acts through.</summary>
/// <remarks>
/// Constructed only where a capability set is built and registered in no container, so no consumer
/// holds a property bag handing out roles it never asked for.
/// <para>
/// A role joins this record with its implementation, never ahead of it, which is the same rule the
/// per-generation capability table follows. A member declared here that nothing implements would be
/// a promise the type could not keep.
/// </para>
/// </remarks>
/// <param name="StudioActing">Monitors a studio.</param>
/// <param name="PerformerActing">Monitors a performer.</param>
/// <param name="MissingSceneActing">Registers scenes an instance's catalogue does not hold.</param>
/// <param name="ReflectOwnedActing">Tells an instance where files the library already holds are.</param>
/// <param name="SearchGrabbing">
/// Asks an instance to look for what it monitors and does not hold. The one role here that can make
/// an instance download, obtained by name and by nothing else.
/// </param>
/// <param name="SceneStatusReading">Reads what an instance holds for one catalogue scene.</param>
/// <param name="SceneExclusionReading">Reads which of a set of scenes the instance's user excluded.</param>
/// <param name="SceneSearchGrabbing">
/// Asks an instance to look for one scene it holds. The second role here that can make an instance
/// download, obtained by name and by nothing else.
/// </param>
/// <param name="SceneMonitorActing">Monitors one scene the instance already holds.</param>
/// <param name="SceneExclusionActing">Excludes one scene, and takes that exclusion back off.</param>
/// <param name="SiteRegistrationActing">
/// Registers a site the instance does not hold, monitoring nothing.
/// </param>
/// <param name="SiteSceneReading">
/// Reads which of a set of scenes one site the instance holds has a row for.
/// </param>
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
    IWhisparrSiteSceneReading SiteSceneReading)
{
    /// <summary>The roles <paramref name="client"/> implements.</summary>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="client"/> implements one of the acting roles this record declares and not
    /// every one of them. The acting roles are implemented on the one type holding this product's
    /// HTTP client, so a client that does not is a registration fault rather than a capability a
    /// generation lacks.
    /// </exception>
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
                siteSceneReading)
            : throw new InvalidOperationException(
                $"{client.GetType()} holds this product's HTTP client but implements only part of "
                    + $"{nameof(WhisparrRoleSet)}.");
    }
}
