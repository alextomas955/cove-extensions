using WhisparrSync.Client;

namespace WhisparrSync.Adapters;

/// <summary>
/// The four file-affecting Whisparr toggles — v3-ONLY. v2's Sonarr-shaped <c>/config/*</c> uses divergent field
/// names, so a v2 adapter never implements this role and the file-settings editor stays v3-only this release.
/// </summary>
internal interface IWhisparrFileSettings
{
    /// <summary>
    /// Reads the four file-affecting toggles off the naming + media-management config singletons — the source of
    /// the config editor's current state.
    /// </summary>
    Task<WhisparrResult<WhisparrFileSettings>> GetFileSettingsAsync(string baseUrl, string apiKey, CancellationToken ct);

    /// <summary>
    /// Writes the four toggles via read-modify-write: GET each config singleton, flip ONLY the booleans
    /// <paramref name="request"/> supplies, then PUT the COMPLETE object back (the unknown fields round-trip
    /// verbatim). A GET that is not Ok short-circuits before any PUT. Returns the resulting settings.
    /// </summary>
    Task<WhisparrResult<WhisparrFileSettings>> EditFileSettingsAsync(
        string baseUrl, string apiKey, WhisparrFileSettingsRequest request, CancellationToken ct);
}
