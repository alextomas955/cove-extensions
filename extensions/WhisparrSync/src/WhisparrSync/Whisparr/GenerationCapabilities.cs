using WhisparrSync.Contracts;

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
    };

    private readonly Dictionary<WhisparrCapability, object> _roles;

    internal WhisparrCapabilitySet(
        WhisparrGeneration generation, Dictionary<WhisparrCapability, object> roles)
    {
        Generation = generation;
        _roles = roles;

        // Declaration order, so the list a browser reads does not vary with how the set was built.
        Held = [.. Enum.GetValues<WhisparrCapability>().Where(roles.ContainsKey)];
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
    /// <typeparamref name="TRole"/> is not one of this product's roles. That says nothing about a
    /// generation, so it is not expressible as a refusal.
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

        return _roles.TryGetValue(capability, out var role)
            ? new Capability<TRole>((TRole)role, null)
            : new Capability<TRole>(null, new CapabilityRefusal(capability, Generation));
    }
}

/// <summary>The capability set each generation is built with.</summary>
/// <remarks>
/// Built from the generation an instance reported rather than registered when the extension loads,
/// because which generation is connected is a stored setting rather than a compile-time fact.
/// </remarks>
public static class GenerationCapabilities
{
    /// <summary>What <paramref name="generation"/> can honour.</summary>
    public static WhisparrCapabilitySet For(WhisparrGeneration generation)
        => new(generation, RolesFor(generation));

    private static Dictionary<WhisparrCapability, object> RolesFor(WhisparrGeneration generation)
        => generation switch
        {
            WhisparrGeneration.V3 => new Dictionary<WhisparrCapability, object>
            {
                [WhisparrCapability.OutOfBandCallbackSecret] = new V3HeaderSecretRegistration(),
            },
            // v2's Webhook connection declares no field for a custom header, so there is nothing to
            // register rather than an implementation that would refuse once it was called.
            _ => [],
        };
}
