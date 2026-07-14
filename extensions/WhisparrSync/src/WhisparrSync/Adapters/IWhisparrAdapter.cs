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

    /// <summary>Lists the full Whisparr movie set (unpaged) — the MATCH-01 reconciliation data source.</summary>
    Task<WhisparrResult<WhisparrMovie[]>> ListMoviesAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>Reads one newest-first page of Whisparr history — the IMPT-02 polling-reconcile data source.</summary>
    Task<WhisparrResult<WhisparrHistoryPage>> ListHistoryAsync(
        string baseUrl, string apiKey, int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Registers the Cove webhook connection for <paramref name="webhookUrl"/>. The adapter owns the
    /// version-specific notification payload shape (implementation / configContract / fields). Best-effort
    /// and single-shot: the underlying transport never blind-retries this non-idempotent call, and a non-2xx
    /// response is a non-Ok result the caller falls back on (copy-paste), never a thrown failure.
    /// </summary>
    Task<WhisparrResult<bool>> RegisterWebhookAsync(string baseUrl, string apiKey, string webhookUrl, CancellationToken ct);
}
