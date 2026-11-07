using System.Globalization;
using System.Text.Json.Nodes;
using V2Api = Whisparr2.Net.Api;

namespace WhisparrSync.Whisparr;

// The webhook connection this product registers on the instance, and the secret it carries.
// Both verbs are hand-composed rather than sent through the generated client, which models the
// notification body as a fixed member set and would drop whatever it does not name.
internal sealed partial class WhisparrV2Instance
{
    // This generation carries the secret in the basic-auth pair on the Webhook connection. The shape
    // is its own type, which has its own tests.
    public OutOfBandSecretField Carry(string secret)
        => new V2BasicAuthSecretRegistration().Carry(secret);

    public Task<WhisparrResponse> ReadNotificationSchemaAsync(CancellationToken ct)
        => GeneratedReadAsync(api => api.Api<V2Api.INotificationApi>().ListNotificationSchemaAsync(ct));

    public Task<WhisparrResponse> ListNotificationsAsync(CancellationToken ct)
        => GeneratedReadAsync(api => api.Api<V2Api.INotificationApi>().ListNotificationAsync(ct));

    public Task<WhisparrResponse> CreateNotificationAsync(JsonNode body, CancellationToken ct)
        => transport.ConfigureAsync(
            binding.BaseAddress,
            binding.ApiKey,
            HttpMethod.Post,
            WhisparrTransport.NotificationPath,
            body,
            ct);

    public Task<WhisparrResponse> UpdateNotificationAsync(int id, JsonNode body, CancellationToken ct)
        => transport.ConfigureAsync(
            binding.BaseAddress,
            binding.ApiKey,
            HttpMethod.Put,
            string.Create(CultureInfo.InvariantCulture, $"{WhisparrTransport.NotificationPath}/{id}"),
            body,
            ct);
}
