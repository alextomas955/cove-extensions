using System.Text.Json;
using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// The version-invariant transport shared by <see cref="V2Adapter"/> and <see cref="V3Adapter"/>: the
/// connect/config delegations, the history read, and the whole webhook-connector admin surface are byte-identical
/// on both generations — v2's <c>/api/v3/notification</c> is the SAME Sonarr-shaped endpoint v3 reads. Landing
/// them here removes the duplicated copies (and the v2/v3 payload drift risk the parity test guarded). A concrete
/// adapter adds only its version-specific surface (the reconcile movie set, the outward push roles).
/// </summary>
internal abstract class WhisparrAdapterBase(WhisparrClient client) : IWhisparrConnection, IWhisparrWebhookAdmin
{
    // The one transport handle both this base and the concrete adapters route every call through.
    protected WhisparrClient Client { get; } = client;

    public Task<WhisparrResult<SystemStatus>> GetStatusAsync(string baseUrl, string apiKey, CancellationToken ct)
        => Client.GetStatusAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<RootFolder[]>> ListRootFoldersAsync(string baseUrl, string apiKey, CancellationToken ct)
        => Client.ListRootFoldersAsync(baseUrl, apiKey, ct);

    public Task<WhisparrResult<WhisparrHistoryPage>> ListHistoryAsync(
        string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct)
        => Client.ListHistoryAsync(baseUrl, apiKey, page, pageSize, ct);

    // Both v3 and v2 read the SAME Sonarr-shaped /api/v3/notification endpoint.
    public async Task<WhisparrResult<WebhookConnection?>> FindWebhookConnectionAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        var listResult = await Client.ListNotificationsAsync(baseUrl, apiKey, ct);
        if (!listResult.IsOk)
        {
            return Propagate<WhisparrNotification[], WebhookConnection?>(listResult);
        }

        var match = Array.Find(
            listResult.Value!,
            n => string.Equals(n.Name, WebhookConnectionName, StringComparison.OrdinalIgnoreCase));
        return match is null
            ? WhisparrResult<WebhookConnection?>.Ok(null)
            : WhisparrResult<WebhookConnection?>.Ok(new WebhookConnection(match.Id, ReadUrlField(match)));
    }

    // Token safety: BuildNotificationPayload re-mints the token from webhookUrl; the existing row's token is
    // never read back or trusted. Idempotency: a unique-name 400 / 409 is a success outcome (the connection
    // already exists — a concurrent create can also race to that conflict), not an error.
    public async Task<WhisparrResult<bool>> RegisterWebhookAsync(string baseUrl, string apiKey, string webhookUrl, CancellationToken ct)
    {
        var existing = await FindWebhookConnectionAsync(baseUrl, apiKey, ct);
        if (!existing.IsOk)
        {
            return Propagate<WebhookConnection?, bool>(existing);
        }

        if (existing.Value is { } connection)
        {
            var update = await Client.UpdateNotificationAsync(
                baseUrl, apiKey, connection.Id, BuildNotificationPayload(webhookUrl, connection.Id), ct);
            return update.IsOk || update.State == WhisparrResultState.Conflict
                ? WhisparrResult<bool>.Ok(true)
                : Propagate<WhisparrNotification, bool>(update);
        }

        var create = await Client.RegisterWebhookAsync(baseUrl, apiKey, BuildNotificationPayload(webhookUrl), ct);
        return create.IsOk || create.State == WhisparrResultState.Conflict
            ? WhisparrResult<bool>.Ok(true)
            : Propagate<bool, bool>(create);
    }

    // Whisparr's notification field values are polymorphic (string / number / bool / array), so read the "url"
    // field's string ONLY when the raw JsonElement actually holds one; null when the row carries no url field.
    private static string? ReadUrlField(WhisparrNotification notification)
    {
        if (notification.Fields is null)
        {
            return null;
        }

        var urlField = Array.Find(
            notification.Fields, f => string.Equals(f.Name, "url", StringComparison.OrdinalIgnoreCase));
        return urlField is { Value.ValueKind: JsonValueKind.String } ? urlField.Value.GetString() : null;
    }

    // Single-sourced connection name: the register write and the find read must key off one literal to agree.
    internal const string WebhookConnectionName = "Cove Whisparr Sync";

    // The Webhook connection payload — identical on v2 and v3 (v2's notification toggle set is a superset, so the
    // shared body is valid on both). `method` value 1 = POST; the secret is delivered as the `X-Cove-Token`
    // header so the receiver authenticates the Test ping. The PUT-update body MUST carry the row id (Whisparr
    // rejects a notification PUT whose body id is absent / does not match the {id} path segment); the POST-create
    // body omits it so Whisparr assigns one. Exposed internally so the register-payload contract is unit-testable.
    internal static string BuildNotificationPayload(string webhookUrl, int? id = null)
    {
        var fields = new object[]
        {
            new { name = "url", value = webhookUrl },
            new { name = "method", value = 1 },
            new { name = "headers", value = new object[] { new { key = "X-Cove-Token", value = ExtractToken(webhookUrl) } } },
        };

        return id is { } rowId
            ? JsonSerializer.Serialize(new
            {
                id = rowId,
                name = WebhookConnectionName,
                implementation = "Webhook",
                implementationName = "Webhook",
                configContract = "WebhookSettings",
                onGrab = false,
                onDownload = true,
                onUpgrade = true,
                onRename = true,
                fields,
            })
            : JsonSerializer.Serialize(new
            {
                name = WebhookConnectionName,
                implementation = "Webhook",
                implementationName = "Webhook",
                configContract = "WebhookSettings",
                onGrab = false,
                onDownload = true,
                onUpgrade = true,
                onRename = true,
                fields,
            });
    }

    // The secret is embedded in the URL's `?token=` query (WebhookUrlBuilder); lift it back out so the header
    // carries the identical value the receiver validates. Returns empty when no token query is present.
    private static string ExtractToken(string webhookUrl)
    {
        const string marker = "?token=";
        var start = webhookUrl.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        var raw = webhookUrl[(start + marker.Length)..];
        var amp = raw.IndexOf('&');
        if (amp >= 0)
        {
            raw = raw[..amp];
        }

        return Uri.UnescapeDataString(raw);
    }

    // Re-shape a non-Ok result of one payload type into the same state for another. The webhook methods reach this
    // only in the propagate-verbatim states (BadKey/NotWhisparr/Unreachable/Rejected — Conflict is handled inline),
    // so the single-sourced state map is exact.
    private static WhisparrResult<TTo> Propagate<TFrom, TTo>(WhisparrResult<TFrom> source)
        => WhisparrResult<TTo>.PropagateFrom(source);
}
