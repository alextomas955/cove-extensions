using WhisparrSync.Connection;

namespace WhisparrSync.Whisparr;

// v2's Webhook schema declares no list-of-headers field, so a custom header is not available on it.
// It sends its user and password fields as an Authorization: Basic header on every delivery, and
// Cove passes that header through to a route declaring the anonymous convention.
//
// The secret is the password half: that is the half the connection stores under a password privacy.
internal sealed class V2BasicAuthSecretRegistration : IOutOfBandSecretRegistration
{
    internal const string UserField = "username";

    internal const string PasswordField = "password";

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
