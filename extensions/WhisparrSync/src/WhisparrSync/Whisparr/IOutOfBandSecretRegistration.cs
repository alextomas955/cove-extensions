namespace WhisparrSync.Whisparr;

/// <summary>
/// Carries a registration secret somewhere other than the address that is registered.
/// </summary>
/// <remarks>
/// A generation holds this role where its Webhook connection declares a settings field, or a pair of
/// them, whose value reaches the callback as a request header. The two generations declare different
/// fields for it and neither is the other's, which is why the role is obtained rather than assumed.
/// </remarks>
public interface IOutOfBandSecretRegistration
{
    /// <summary>The registration field values that carry <paramref name="secret"/> off the address.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="secret"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is empty or whitespace.</exception>
    OutOfBandSecretField Carry(string secret);
}

/// <summary>One value a Webhook registration sets, in the shape that field's own schema declares.</summary>
/// <param name="Name">The settings field's name, as the schema declares it.</param>
/// <param name="Value">The value, in whatever JSON shape that field takes.</param>
public sealed record WhisparrFieldValue(string Name, object Value);

/// <summary>How one generation carries a secret off the address it registers.</summary>
/// <param name="Fields">The registration field values to set. One field on one generation, two on the other.</param>
/// <param name="ArrivesAsHeader">The request header a callback from this instance carries the secret in.</param>
public sealed record OutOfBandSecretField(
    IReadOnlyList<WhisparrFieldValue> Fields,
    string ArrivesAsHeader);
