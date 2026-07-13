using System.Text.Json;
using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// The Whisparr v3 ("Eros") adapter (VER-02): implements the connect flow against the <c>/api/v3</c>
/// surface by delegating each call to the transport-only <see cref="WhisparrClient"/>. All v3-specific
/// wire knowledge (which client endpoint answers each port method) lives here, not in the handlers — so a
/// future v2 adapter slots behind the same <see cref="IWhisparrAdapter"/> port without touching callers.
/// </summary>
internal sealed class V3Adapter(WhisparrClient client) : IWhisparrAdapter
{
    public Task<WhisparrResult<SystemStatus>> GetStatusAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.GetStatusAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<RootFolder[]>> ListRootFoldersAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.ListRootFoldersAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<QualityProfile[]>> ListQualityProfilesAsync(string baseUrl, string apiKey, CancellationToken ct)
        => client.ListQualityProfilesAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<bool>> RegisterWebhookAsync(string baseUrl, string apiKey, string webhookUrl, CancellationToken ct)
        => client.RegisterWebhookAsync(baseUrl, apiKey, BuildNotificationPayload(webhookUrl), ct);

    // The v3 Webhook connection payload (RESEARCH.md §Webhook auto-register / Assumption A1). The exact
    // `fields` contract is best-effort — if this Whisparr build rejects it the connect flow still succeeds
    // via the copy-paste URL. `method` value 1 = POST (Servarr WebhookMethod enum).
    private static string BuildNotificationPayload(string webhookUrl)
        => JsonSerializer.Serialize(new
        {
            name = "Cove Whisparr Sync",
            implementation = "Webhook",
            implementationName = "Webhook",
            configContract = "WebhookSettings",
            onGrab = false,
            onDownload = true,
            onUpgrade = true,
            onRename = true,
            fields = new object[]
            {
                new { name = "url", value = webhookUrl },
                new { name = "method", value = 1 },
            },
        });
}
