using WhisparrSync.Connection;

namespace WhisparrSync.Whisparr;

/// <summary>
/// The newer generation's carrier: a custom request header, set through the Webhook connection's
/// list-of-headers settings field.
/// </summary>
/// <remarks>
/// The field name is the one that generation's own notification schema declares; the header name is
/// this product's, declared once on the inbound side that reads it.
/// </remarks>
internal sealed class V3HeaderSecretRegistration : IOutOfBandSecretRegistration
{
    /// <summary>The Webhook settings field holding a list of custom headers.</summary>
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
