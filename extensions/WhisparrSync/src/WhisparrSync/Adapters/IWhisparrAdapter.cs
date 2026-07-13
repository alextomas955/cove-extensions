using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// The version-adapter port: the anti-corruption boundary between the settings handlers and a specific
/// Whisparr API generation (VER-01). Every Whisparr call routes through this seam, so the handlers never
/// hold generation-specific wire knowledge — the concrete adapter (e.g. <see cref="V3Adapter"/>) owns the
/// endpoint paths + DTO mapping for its major version. The <see cref="AdapterSelector"/> chooses the
/// implementation from the detected version and refuses a version it cannot manage (VER-04), so a caller
/// that holds an <see cref="IWhisparrAdapter"/> is already guaranteed a version this build supports.
/// </summary>
internal interface IWhisparrAdapter
{
    /// <summary>Reads the instance status (version + instance name) for the connect flow.</summary>
    Task<WhisparrResult<SystemStatus>> GetStatusAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>Lists the configured root folders (for the settings dropdown).</summary>
    Task<WhisparrResult<RootFolder[]>> ListRootFoldersAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>Lists the configured quality profiles (for the settings dropdown).</summary>
    Task<WhisparrResult<QualityProfile[]>> ListQualityProfilesAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>
    /// Registers the Cove webhook connection from a pre-serialized notification payload. Single-shot: the
    /// underlying transport never blind-retries this non-idempotent call.
    /// </summary>
    Task<WhisparrResult<bool>> RegisterWebhookAsync(string baseUrl, string apiKey, string notificationJson, CancellationToken ct);
}
