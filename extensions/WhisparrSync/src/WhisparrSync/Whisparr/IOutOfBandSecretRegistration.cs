namespace WhisparrSync.Whisparr;

/// <summary>
/// Carries a registration secret somewhere other than the address that is registered.
/// </summary>
/// <remarks>
/// A generation holds this role only where its Webhook connection declares a settings field for a
/// custom header. Whisparr v2 declares no such field, so on that generation a secret has nowhere out
/// of band to go and there is no implementation to obtain.
/// </remarks>
public interface IOutOfBandSecretRegistration
{
    /// <summary>The registration field value that carries <paramref name="secret"/> off the address.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="secret"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="secret"/> is empty or whitespace.</exception>
    OutOfBandSecretField Carry(string secret);
}

/// <summary>One registration field value, in the shape that field's own schema declares.</summary>
/// <param name="FieldName">The settings field the value belongs to.</param>
/// <param name="HeaderName">The header a callback from this instance arrives with.</param>
/// <param name="HeaderValue">The secret, as that header's value.</param>
public sealed record OutOfBandSecretField(string FieldName, string HeaderName, string HeaderValue);

/// <inheritdoc cref="IOutOfBandSecretRegistration"/>
internal sealed class V3HeaderSecretRegistration : IOutOfBandSecretRegistration
{
    /// <summary>The Webhook settings field holding a list of custom headers.</summary>
    internal const string HeadersField = "headers";

    /// <summary>The header this product's own callbacks are recognised by.</summary>
    internal const string HeaderName = "X-Cove-Whisparr-Sync-Secret";

    public OutOfBandSecretField Carry(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return new OutOfBandSecretField(HeadersField, HeaderName, secret);
    }
}
