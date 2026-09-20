using WhisparrSync.Connection;

namespace WhisparrSync.Whisparr;

// v3 carries the secret in a custom request header, set through its Webhook connection's
// list-of-headers settings field. The header name is this extension's, declared on the inbound side.
internal sealed class V3HeaderSecretRegistration : IOutOfBandSecretRegistration
{
    internal const string HeadersField = "headers";

    public OutOfBandSecretField Carry(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        return new OutOfBandSecretField(
            [
                new WhisparrFieldValue(
                    HeadersField,
                    new[] { new { key = CallbackSecret.CustomHeaderName, value = secret } }),
            ],
            CallbackSecret.CustomHeaderName);
    }
}
