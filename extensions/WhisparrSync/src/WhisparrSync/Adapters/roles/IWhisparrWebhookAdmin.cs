using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// The webhook connector admin surface. Both v3 and v2 read/write the same Sonarr-shaped
/// <c>/api/v3/notification</c>, so this role is version-invariant and lands on
/// <c>WhisparrAdapterBase</c>.
/// </summary>
internal interface IWhisparrWebhookAdmin
{
    /// <summary>
    /// Reads the extension's own "Cove Whisparr Sync" connection from Whisparr's notification list and projects
    /// it to a <see cref="WebhookConnection"/> (its row id + the URL embedded in the <c>url</c> field). Matches on
    /// the SAME connection-name literal <see cref="RegisterWebhookAsync"/> writes. An empty/non-matching list is
    /// an <see cref="WhisparrResultState.Ok"/> result with a <c>null</c> connection ("not registered yet").
    /// </summary>
    Task<WhisparrResult<WebhookConnection?>> FindWebhookConnectionAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>
    /// Idempotently registers the Cove webhook connection for <paramref name="webhookUrl"/> (update-or-create):
    /// PUT-updates the existing connection in place when present, else POST-creates it. The token is ALWAYS
    /// re-minted from <paramref name="webhookUrl"/> — never read back from the existing connection — and a
    /// unique-name 400 / 409 resolves to success (the connection exists), never an error.
    /// </summary>
    Task<WhisparrResult<bool>> RegisterWebhookAsync(string baseUrl, string apiKey, string webhookUrl, CancellationToken ct);
}
