namespace WhisparrSync.Whisparr;

/// <summary>
/// Carries a registration secret somewhere other than the address that is registered.
/// </summary>
/// <remarks>
/// A generation holds this role where its Webhook connection declares a settings field, or a pair of
/// them, whose value reaches the callback as a request header. v2 and v3 declare different fields
/// for it, so the role is obtained rather than assumed.
/// </remarks>
public interface IOutOfBandSecretRegistration
{
    /// <summary>The registration field values that carry <paramref name="secret"/> off the address.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="secret"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is empty or whitespace.</exception>
    OutOfBandSecretField Carry(string secret);
}

/// <summary>One value a Webhook registration sets, in the shape that field's own schema declares.</summary>
public sealed record WhisparrFieldValue(string Name, object Value);

/// <summary>How one generation carries a secret off the address it registers.</summary>
/// <remarks>
/// One field to set on v3, two on v2. The header is the one a callback from this instance carries
/// the secret in.
/// </remarks>
public sealed record OutOfBandSecretField(
    IReadOnlyList<WhisparrFieldValue> Fields,
    string ArrivesAsHeader);
