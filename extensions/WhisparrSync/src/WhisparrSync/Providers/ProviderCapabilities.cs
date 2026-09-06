namespace WhisparrSync.Providers;

/// <summary>What one metadata provider can be asked to do.</summary>
/// <remarks>
/// A capability is written down here only once some provider has an implementation to register for
/// it. Registered ahead of one it would name a capability whose only possible answer is a fault.
/// </remarks>
public enum ProviderCapability
{
    /// <summary>The catalogue can be ordered by title.</summary>
    SortByTitle,

    /// <summary>The catalogue can be ordered by release date.</summary>
    SortByDate,

    /// <summary>The catalogue can be ordered by duration.</summary>
    SortByDuration,

    /// <summary>The catalogue can be narrowed to a year exactly.</summary>
    FilterByYear,

    /// <summary>The performers in a catalogue can be listed and filtered by.</summary>
    ListPerformerFacet,

    /// <summary>The tags in a catalogue can be listed and filtered by.</summary>
    ListTagFacet,

    /// <summary>A studio's own sub-studios can be listed and filtered by.</summary>
    ListSubStudioFacet,

    /// <summary>Titles can be searched over the whole catalogue.</summary>
    SearchTitles,

    /// <summary>An entity can be looked up by its exact name.</summary>
    LookUpByName,
}

/// <summary>The catalogue can be ordered by title.</summary>
public interface ISortsByTitle;

/// <summary>The catalogue can be ordered by release date.</summary>
public interface ISortsByDate;

/// <summary>The catalogue can be ordered by duration.</summary>
public interface ISortsByDuration;

/// <summary>The catalogue can be narrowed to a year exactly.</summary>
/// <remarks>
/// Held by a provider that filters on a year and by no provider that only approximates one. An
/// approximate answer under an exact name is a wrong answer a reader would read as right.
/// </remarks>
public interface IFiltersByYear;

/// <summary>The performers in a catalogue can be listed and filtered by.</summary>
public interface IListsPerformerFacet;

/// <summary>The tags in a catalogue can be listed and filtered by.</summary>
public interface IListsTagFacet;

/// <summary>A studio's own sub-studios can be listed and filtered by.</summary>
public interface IListsSubStudioFacet;

/// <summary>Titles can be searched over the whole catalogue.</summary>
public interface ISearchesTitles;

/// <summary>An entity can be looked up by its exact name.</summary>
public interface ILooksUpByName;

/// <summary>The capability set each provider holds.</summary>
/// <remarks>
/// A role is registered here only where the provider has been measured to honour it. A capability
/// absent from a set is one the surface offers no control for at all.
/// </remarks>
internal static class ProviderCapabilities
{
    /// <summary>What StashDB holds, acting through <paramref name="source"/>.</summary>
    internal static ProviderCapabilitySet ForStashDb(object source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new ProviderCapabilitySet(
            "StashDB",
            [ProviderCapability.SortByDate],
            new Dictionary<ProviderCapability, object>
            {
                [ProviderCapability.SortByDate] = source,
            });
    }
}

/// <summary>A capability the provider does not hold.</summary>
/// <param name="Capability">The capability that was asked for.</param>
/// <param name="Provider">The provider it was refused on.</param>
public sealed record ProviderCapabilityRefusal(ProviderCapability Capability, string Provider);

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
    private readonly ProviderCapabilityRefusal? _refusal;

    internal Capability(TRole? role, ProviderCapabilityRefusal? refusal)
    {
        _role = role;
        _refusal = refusal;
    }

    /// <summary>
    /// Applies <paramref name="held"/> to the role, or <paramref name="refused"/> to the refusal.
    /// </summary>
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
    };

    private readonly Dictionary<ProviderCapability, object> _roles;

    /// <summary>The set <paramref name="provider"/> holds, acting through <paramref name="roles"/>.</summary>
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

    /// <summary>The provider this set was built for.</summary>
    public string Provider { get; }

    /// <summary>The capabilities that provider holds.</summary>
    public IReadOnlyList<ProviderCapability> Held { get; }

    /// <summary>The role <typeparamref name="TRole"/>, or the refusal standing in its place.</summary>
    /// <typeparam name="TRole">The role asked for.</typeparam>
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

        // A capability the provider HOLDS, asked of a set built without the source implementing it,
        // is a construction fault. Answered as a refusal it would be indistinguishable from a real
        // provider gap, which is the silent-bug class the capability split exists to remove.
        if (Held.Contains(capability))
        {
            throw new InvalidOperationException(
                $"{Provider} holds {capability}, but this capability set was built with no source for "
                    + $"{typeof(TRole)}.");
        }

        return new Capability<TRole>(null, new ProviderCapabilityRefusal(capability, Provider));
    }
}
