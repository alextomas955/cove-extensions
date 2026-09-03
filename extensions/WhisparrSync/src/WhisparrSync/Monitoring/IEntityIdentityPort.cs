using WhisparrSync.Contracts;

namespace WhisparrSync.Monitoring;

/// <summary>The identifier one entity is known by in a generation's namespace, or why it has none.</summary>
/// <param name="ForeignId">The identifier to name the entity by, or null on a refusal.</param>
/// <param name="Refusal">Why there is none, or <see cref="MonitorRefusalKind.None"/>.</param>
public sealed record IdentityResolution(string? ForeignId, MonitorRefusalKind Refusal)
{
    /// <summary>The entity carries no identifier in the namespace asked about.</summary>
    public static IdentityResolution Unmatched { get; } =
        new(null, MonitorRefusalKind.NoIdentityInThisNamespace);

    /// <summary>
    /// The entity carries several different identifiers in the namespace asked about.
    /// </summary>
    /// <remarks>
    /// Answered instead of one of them. Which entity the outbound request would name depends on
    /// which row was read first, and row order is not something a caller or a reader chose.
    /// </remarks>
    public static IdentityResolution Ambiguous { get; } =
        new(null, MonitorRefusalKind.SeveralIdentitiesInThisNamespace);

    /// <summary>The entity is named by <paramref name="foreignId"/>.</summary>
    public static IdentityResolution At(string foreignId)
        => new(foreignId, MonitorRefusalKind.None);
}

/// <summary>
/// Reads the identifier an entity is known by in the connected generation's metadata namespace.
/// </summary>
/// <remarks>
/// The only source of an outbound identifier. No caller supplies one, so aiming this extension's
/// credential at an entity of someone's choosing is not expressible: the caller names a Cove entity
/// and this reads what the library itself holds for it.
/// </remarks>
public interface IEntityIdentityPort
{
    /// <summary>
    /// What the <paramref name="kind"/> entity <paramref name="coveId"/> names is known as in
    /// <paramref name="generation"/>'s namespace.
    /// </summary>
    /// <remarks>
    /// The namespace is chosen by the connected generation rather than by preference: the two
    /// generations identify entities in namespaces neither shares with the other, so a row in one is
    /// no identity at all in the other.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> is not a kind this product expresses. A kind resolving to another
    /// kind's identity table would aim this extension's credential at an unrelated entity.
    /// </exception>
    Task<IdentityResolution> ResolveAsync(
        WhisparrEntityKind kind, int coveId, WhisparrGeneration generation, CancellationToken ct);
}
