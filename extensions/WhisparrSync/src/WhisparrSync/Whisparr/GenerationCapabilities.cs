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
    /// and this set was built with no instance implementing it. Neither is a generation gap, so
    /// neither is expressible as a refusal.
    /// </exception>
    public Capability<TRole> Obtain<TRole>()
        where TRole : class
    {
        if (GenerationCapabilities.CapabilityFor(typeof(TRole)) is not { } capability)
        {
            throw new InvalidOperationException(
                $"{typeof(TRole)} is not a Whisparr capability role. Add it to the role table in "
                    + $"{nameof(GenerationCapabilities)}, beside the capability it expresses.");
        }

        if (_roles.TryGetValue(capability, out var role))
        {
            return new Capability<TRole>((TRole)role, null);
        }

        // A capability the generation holds, asked of a set built with no instance implementing it,
        // is a construction fault. As a refusal it would read as a real generation gap.
        if (Held.Contains(capability))
        {
            throw new InvalidOperationException(
                $"{Generation} holds {capability}, but this capability set was built with no source for "
                    + $"{typeof(TRole)}. Build it through the overload taking the bound instance.");
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
    // One row per role this product can obtain: the capability it is refused under, and where its
    // implementation comes from. A generation that does not hold the capability registers no row of
    // its own, so the role is absent rather than present and refusing when it is called.
    //
    // The role type and the source are tied by the compiler, so a row cannot name one role and hand
    // out another. Every row but the first sources its role by testing the bound instance for the
    // interface, so a role the instance's generation does not hold has nothing to answer with.
    private static readonly RoleEntry[] Roles =
    [
        // Both generations carry a secret off the address, through fields they do not share: a
        // list-of-headers field on v3, a user-and-password pair on v2. Neither sends anything, so this
        // row answers whether or not a bound instance was supplied.
        RoleEntry.Of<IOutOfBandSecretRegistration>(
            WhisparrCapability.OutOfBandCallbackSecret,
            static (generation, _) => generation == WhisparrGeneration.V3
                ? new V3HeaderSecretRegistration()
                : new V2BasicAuthSecretRegistration()),
        RoleEntry.Of<IWhisparrStudioActing>(
            WhisparrCapability.MonitorStudio,
            static (_, instance) => instance as IWhisparrStudioActing),
        RoleEntry.Of<IWhisparrPerformerActing>(
            WhisparrCapability.MonitorPerformer,
            static (_, instance) => instance as IWhisparrPerformerActing),
        RoleEntry.Of<IWhisparrMissingSceneActing>(
            WhisparrCapability.RegisterMissingScenes,
            static (_, instance) => instance as IWhisparrMissingSceneActing),
        RoleEntry.Of<IWhisparrSiteRegistrationActing>(
            WhisparrCapability.RegisterOwnedSites,
            static (_, instance) => instance as IWhisparrSiteRegistrationActing),
        RoleEntry.Of<IWhisparrReflectOwnedActing>(
            WhisparrCapability.ReflectOwnedFiles,
            static (_, instance) => instance as IWhisparrReflectOwnedActing),
        RoleEntry.Of<IWhisparrSearchGrabbing>(
            WhisparrCapability.SearchMonitored,
            static (_, instance) => instance as IWhisparrSearchGrabbing),
        RoleEntry.Of<IWhisparrSceneStatusReading>(
            WhisparrCapability.ReadSceneStatus,
            static (_, instance) => instance as IWhisparrSceneStatusReading),
        RoleEntry.Of<IWhisparrSceneExclusionReading>(
            WhisparrCapability.ReadSceneExclusions,
            static (_, instance) => instance as IWhisparrSceneExclusionReading),
        RoleEntry.Of<IWhisparrSceneSearchGrabbing>(
            WhisparrCapability.SearchScene,
            static (_, instance) => instance as IWhisparrSceneSearchGrabbing),
        RoleEntry.Of<IWhisparrSceneMonitorActing>(
            WhisparrCapability.MonitorScene,
            static (_, instance) => instance as IWhisparrSceneMonitorActing),
        RoleEntry.Of<IWhisparrSceneExclusionActing>(
            WhisparrCapability.ExcludeScene,
            static (_, instance) => instance as IWhisparrSceneExclusionActing),
        RoleEntry.Of<IWhisparrSiteSceneReading>(
            WhisparrCapability.ReadSiteSceneRows,
            static (_, instance) => instance as IWhisparrSiteSceneReading),
        RoleEntry.Of<IWhisparrHeldSiteReading>(
            WhisparrCapability.ReadHeldSites,
            static (_, instance) => instance as IWhisparrHeldSiteReading),
        RoleEntry.Of<IWhisparrEntityBatchReading>(
            WhisparrCapability.ReadEntityCardsInBatch,
            static (_, instance) => instance as IWhisparrEntityBatchReading),
        RoleEntry.Of<IWhisparrSceneBatchReading>(
            WhisparrCapability.ReadSceneCardsInBatch,
            static (_, instance) => instance as IWhisparrSceneBatchReading),
        RoleEntry.Of<IWhisparrEntityTrackingActing>(
            WhisparrCapability.TrackEntityCatalogue,
            static (_, instance) => instance as IWhisparrEntityTrackingActing),
        RoleEntry.Of<IWhisparrEntityCatalogueReading>(
            WhisparrCapability.ReadEntityCatalogue,
            static (_, instance) => instance as IWhisparrEntityCatalogueReading),
        RoleEntry.Of<IWhisparrInstanceFilesystemReading>(
            WhisparrCapability.ReadInstanceFilesystem,
            static (_, instance) => instance as IWhisparrInstanceFilesystemReading),
    ];

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

    /// <summary>What <paramref name="generation"/> can honour, with no instance supplied.</summary>
    /// <remarks>
    /// For a caller that only needs to know what the generation can do, or one of the capabilities
    /// that sends nothing. Asking this set for an acting role throws rather than refusing,
    /// because a set built with no source for a capability the generation holds is a construction
    /// fault and not a generation gap.
    /// </remarks>
    public static WhisparrCapabilitySet For(WhisparrGeneration generation)
        => new(generation, RolesFor(generation, null));

    /// <summary>What <paramref name="generation"/> can honour, acting through the bound instance.</summary>
    /// <remarks>
    /// The instance declares only the roles its own generation holds, so a role this set hands out is
    /// one the instance has a member for. A generation whose declared capabilities and whose
    /// instance's interfaces disagree is a fault <see cref="WhisparrCapabilitySet.Obtain"/> states.
    /// </remarks>
    internal static WhisparrCapabilitySet For(WhisparrGeneration generation, IWhisparrClient instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return new WhisparrCapabilitySet(generation, RolesFor(generation, instance));
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

    // Indexed off the table above rather than written out again, so neither index can disagree
    // with it.
    private static readonly Dictionary<Type, WhisparrCapability> CapabilityByRole =
        Roles.ToDictionary(entry => entry.Role, entry => entry.Capability);

    private static readonly Dictionary<WhisparrCapability, RoleEntry> RoleByCapability =
        Roles.ToDictionary(entry => entry.Capability);

    /// <summary>
    /// The capability <paramref name="role"/> is obtained under, or null where it is no role of
    /// this product's.
    /// </summary>
    internal static WhisparrCapability? CapabilityFor(Type role)
        => CapabilityByRole.TryGetValue(role, out var capability) ? capability : null;

    // Registered off the capability list, so what a generation holds and what it acts through
    // cannot answer differently. A role the instance does not implement is left out, which is what
    // makes a capability the generation declares but the instance cannot honour a stated fault.
    private static Dictionary<WhisparrCapability, object> RolesFor(
        WhisparrGeneration generation, IWhisparrClient? instance)
    {
        var registered = new Dictionary<WhisparrCapability, object>();
        foreach (var capability in CapabilitiesOf(generation))
        {
            if (RoleByCapability[capability].Source(generation, instance) is { } role)
            {
                registered[capability] = role;
            }
        }

        return registered;
    }

    private sealed record RoleEntry(
        WhisparrCapability Capability,
        Type Role,
        Func<WhisparrGeneration, IWhisparrClient?, object?> Source)
    {
        internal static RoleEntry Of<TRole>(
            WhisparrCapability capability, Func<WhisparrGeneration, IWhisparrClient?, TRole?> source)
            where TRole : class
            => new(capability, typeof(TRole), source);
    }
}
