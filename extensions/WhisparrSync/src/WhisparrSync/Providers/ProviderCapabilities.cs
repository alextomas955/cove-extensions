namespace WhisparrSync.Providers;

/// <summary>What one metadata provider can be asked to do.</summary>
/// <remarks>
/// A capability is written down here only once some provider has an implementation to register for
/// it. Registered ahead of one, its only possible answer is a fault.
/// </remarks>
public enum ProviderCapability
{
    SortByTitle,
    SortByDate,
    SortByDuration,
    FilterByYear,
    ListPerformerFacet,
    ListTagFacet,
    ListSubStudioFacet,
    SearchTitles,
    LookUpByName,
    ResolveNumericSceneId,
    ResolveNumericSiteId,
    ReadSceneCover,
}

public interface ISortsByTitle;

public interface ISortsByDate;

public interface ISortsByDuration;

/// <summary>The catalogue can be narrowed to a year exactly.</summary>
/// <remarks>
/// Not held by a provider that only approximates a year. An approximate answer under an exact name
/// is a wrong answer a reader would read as right.
/// </remarks>
public interface IFiltersByYear;

public interface IListsPerformerFacet;

public interface IListsTagFacet;

public interface IListsSubStudioFacet;

public interface ISearchesTitles;

public interface ILooksUpByName;

/// <summary>A stored scene identifier can be resolved to the provider's own numeric id.</summary>
/// <remarks>
/// A provider that issues no number of its own does not implement this interface, so the
/// resolution is refused before any request rather than answered as a scene the provider does not
/// name.
/// </remarks>
public interface IResolvesNumericSceneId;

/// <summary>A stored site identifier can be resolved to the provider's own numeric id.</summary>
/// <remarks>
/// A provider that issues no number of its own does not implement this interface, so the
/// resolution is refused before any request rather than answered as a site the provider does not
/// name.
/// </remarks>
public interface IResolvesNumericSiteId;

/// <summary>A scene's cover picture can be read from the provider by its stored identifier.</summary>
/// <remarks>
/// The member is declared here and not on <see cref="IProviderCatalogue"/>, so a provider holding
/// no cover picture has nothing to write.
/// </remarks>
public interface IReadsSceneCover
{
    Task<string?> ReadSceneCoverAsync(string providerSceneId, CancellationToken ct);
}

internal static class ProviderCapabilities
{
    // Ordering by title is StashDB's alone: ThePornDB's ordering vocabulary declares no title value
    // and refuses one it does not declare. Filtering to a year is ThePornDB's alone: StashDB carries
    // one date criterion with no inclusive bound, so a year is not expressible on it at all.
    private static readonly ProviderCapability[] StashDbHolds =
    [
        ProviderCapability.SortByTitle,
        ProviderCapability.SortByDate,
        ProviderCapability.SortByDuration,
        ProviderCapability.ListPerformerFacet,
        ProviderCapability.ListTagFacet,
        ProviderCapability.ListSubStudioFacet,
        ProviderCapability.SearchTitles,
        ProviderCapability.LookUpByName,
    ];

    // Neither the performer route nor the site route exposes a filter that would scope its values
    // to one entity, so neither of those menus is listable here. Resolving a scene or a site to a
    // number is this provider's alone: its scene rows carry an `_id` beside the uuid Cove stores,
    // and its site route answers an `id` beside the same uuid. StashDB names both by uuid and by
    // nothing else, so it issues no such number to resolve to.
    private static readonly ProviderCapability[] ThePornDbHolds =
    [
        ProviderCapability.SortByDate,
        ProviderCapability.SortByDuration,
        ProviderCapability.FilterByYear,
        ProviderCapability.ListTagFacet,
        ProviderCapability.SearchTitles,
        ProviderCapability.LookUpByName,
        ProviderCapability.ResolveNumericSceneId,
        ProviderCapability.ResolveNumericSiteId,
        ProviderCapability.ReadSceneCover,
    ];

    internal static ProviderCapabilitySet ForStashDb(object source)
        => SetFor("StashDB", StashDbHolds, source);

    internal static ProviderCapabilitySet ForThePornDb(object source)
        => SetFor("ThePornDB", ThePornDbHolds, source);

    private static ProviderCapabilitySet SetFor(
        string provider, IReadOnlyList<ProviderCapability> held, object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ProviderCapabilitySet(
            provider, held, held.ToDictionary(capability => capability, _ => source));
    }
}

/// <summary>A capability the provider does not hold.</summary>
public sealed record ProviderCapabilityRefusal(ProviderCapability Capability, string Provider);

/// <summary>A role obtained from a capability set, or the refusal standing in its place.</summary>
/// <remarks>
/// There is no third answer, and no way to reach the role without also stating what happens when it
/// is absent.
/// </remarks>
public sealed class Capability<TRole>
    where TRole : class
{
    private readonly TRole? _role;
    private readonly ProviderCapabilityRefusal? _refusal;

    internal Capability(TRole? role, ProviderCapabilityRefusal? refusal)
    {
        _role = role;
        _refusal = refusal;
    }

    public TResult Match<TResult>(
        Func<TRole, TResult> held, Func<ProviderCapabilityRefusal, TResult> refused)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(refused);
        return _role is not null ? held(_role) : refused(_refusal!);
    }
}

/// <summary>The roles one metadata provider can honour.</summary>
/// <remarks>
/// A role a provider cannot honour is absent rather than present and refusing when it is called, so
/// a caller has no implementation to obtain and no way to express the request. A sort option, a
/// facet and the year filter are therefore absences on the toolbar rather than dimmed controls.
/// </remarks>
public sealed class ProviderCapabilitySet
{
    // Every role this product declares, whatever any provider holds: an absent role still needs a
    // capability to be refused under.
    private static readonly Dictionary<Type, ProviderCapability> CapabilityByRole = new()
    {
        [typeof(ISortsByTitle)] = ProviderCapability.SortByTitle,
        [typeof(ISortsByDate)] = ProviderCapability.SortByDate,
        [typeof(ISortsByDuration)] = ProviderCapability.SortByDuration,
        [typeof(IFiltersByYear)] = ProviderCapability.FilterByYear,
        [typeof(IListsPerformerFacet)] = ProviderCapability.ListPerformerFacet,
        [typeof(IListsTagFacet)] = ProviderCapability.ListTagFacet,
        [typeof(IListsSubStudioFacet)] = ProviderCapability.ListSubStudioFacet,
        [typeof(ISearchesTitles)] = ProviderCapability.SearchTitles,
        [typeof(ILooksUpByName)] = ProviderCapability.LookUpByName,
        [typeof(IResolvesNumericSceneId)] = ProviderCapability.ResolveNumericSceneId,
        [typeof(IResolvesNumericSiteId)] = ProviderCapability.ResolveNumericSiteId,
        [typeof(IReadsSceneCover)] = ProviderCapability.ReadSceneCover,
    };

    private readonly Dictionary<ProviderCapability, object> _roles;

    /// <remarks>
    /// <paramref name="held"/> is what the provider can honour, which is not what this set happened
    /// to be built with: a set built for a read would otherwise report that the provider cannot do
    /// what it can.
    /// </remarks>
    public ProviderCapabilitySet(
        string provider,
        IReadOnlyList<ProviderCapability> held,
        Dictionary<ProviderCapability, object> roles)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(roles);
        Provider = provider;
        Held = held;
        _roles = roles;
    }

    public string Provider { get; }

    public IReadOnlyList<ProviderCapability> Held { get; }

    /// <summary>The role <typeparamref name="TRole"/>, or the refusal standing in its place.</summary>
    /// <exception cref="InvalidOperationException">
    /// <typeparamref name="TRole"/> is not one of this product's roles, or it is one this provider
    /// holds and this set was built without the source that implements it. Neither says anything
    /// about a provider, so neither is expressible as a refusal.
    /// </exception>
    public Capability<TRole> Obtain<TRole>()
        where TRole : class
    {
        if (!CapabilityByRole.TryGetValue(typeof(TRole), out var capability))
        {
            throw new InvalidOperationException(
                $"{typeof(TRole)} is not a provider capability role. Add it to {nameof(CapabilityByRole)} "
                    + "beside the capability it expresses.");
        }

        if (_roles.TryGetValue(capability, out var role))
        {
            return new Capability<TRole>((TRole)role, null);
        }

        // A capability the provider does hold, asked of a set built without the source implementing
        // it, is a construction fault. Answered as a refusal it would be indistinguishable from a
        // real provider gap.
        if (Held.Contains(capability))
        {
            throw new InvalidOperationException(
                $"{Provider} holds {capability}, but this capability set was built with no source for "
                    + $"{typeof(TRole)}.");
        }

        return new Capability<TRole>(null, new ProviderCapabilityRefusal(capability, Provider));
    }
}
