using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// The version-invariant connect + config surface both Whisparr generations answer identically — the home
/// the shared transport (<c>WhisparrAdapterBase</c>) lands on. Every adapter (v2 and v3) implements
/// this role.
/// </summary>
internal interface IWhisparrConnection
{
    /// <summary>Reads the instance status (version + instance name) for the connect flow.</summary>
    Task<WhisparrResult<SystemStatus>> GetStatusAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>Lists the configured root folders (for the settings dropdown).</summary>
    Task<WhisparrResult<RootFolder[]>> ListRootFoldersAsync(string baseUrl, string apiKey, CancellationToken ct);
}
