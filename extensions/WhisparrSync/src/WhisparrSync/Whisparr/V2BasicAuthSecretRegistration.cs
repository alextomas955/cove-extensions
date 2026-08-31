using WhisparrSync.Connection;

namespace WhisparrSync.Whisparr;

/// <summary>
/// The older generation's carrier: the Webhook connection's user and password fields, which that
/// generation sends as an <c>Authorization: Basic</c> header on every delivery.
/// </summary>
/// <remarks>
/// This generation's schema declares no list-of-headers field, so a custom header is not available
/// on it. That it sends an authorization header from these two fields, and that Cove passes one
/// through to a route declaring the anonymous convention, are both measurements rather than
/// inferences — see the fixture ledger's row for the out-of-band question on this generation.
/// <para>
/// The secret is the PASSWORD half. That is the half the connection stores under a password privacy,
/// and the user name identifies the registration rather than authorising it.
/// </para>
/// </remarks>
internal sealed class V2BasicAuthSecretRegistration : IOutOfBandSecretRegistration
{
    /// <summary>The Webhook settings field holding the Basic-auth user name.</summary>
    internal const string UserField = "username";

    /// <summary>The Webhook settings field holding the Basic-auth password.</summary>
    internal const string PasswordField = "password";

    /// <summary>The header a Basic-auth credential arrives in.</summary>
    internal const string AuthorizationHeader = "Authorization";

    public OutOfBandSecretField Carry(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return new OutOfBandSecretField(
            [
                new WhisparrFieldValue(UserField, CallbackSecret.BasicAuthUser),
                new WhisparrFieldValue(PasswordField, secret),
            ],
            AuthorizationHeader);
    }
}
