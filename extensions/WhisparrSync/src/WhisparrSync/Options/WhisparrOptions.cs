using System.Text.Json;
using System.Text.Json.Serialization;

namespace WhisparrSync.Options;

/// <summary>
/// The persisted Whisparr Sync configuration, serialized as a single forward-compatible
/// <c>System.Text.Json</c> blob under the <c>"options"</c> store key (unknown props ignored on load,
/// missing props default). A flat scalar record, so the compiler-generated value equality is exactly
/// right — no hand-written <c>Equals</c>/<c>GetHashCode</c> is needed.
/// </summary>
/// <remarks>
/// CONN-06: <see cref="ApiKey"/> and <see cref="WebhookSecret"/> live at rest in Cove's plaintext
/// key-value store. The key is used server-side only — it is projected out of every response through
/// <see cref="OptionsView"/> (which exposes a <c>hasApiKey</c> boolean instead) and never appears in a log.
/// </remarks>
public sealed record WhisparrOptions
{
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string SelectedVersion { get; init; } = "v3";
    public string DetectedVersion { get; init; } = "";
    public int RootFolderId { get; init; }
    public int QualityProfileId { get; init; }
    public string WebhookSecret { get; init; } = "";

    /// <summary>
    /// Shared serializer settings for the load/save round-trip: case-insensitive property names so a
    /// hand-edited blob still binds. <c>OptionsStore</c> reuses this exact instance.
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Returns a copy with the user-submitted settings applied. Write-only key semantics: an empty or
    /// absent submitted <paramref name="apiKey"/> PRESERVES the stored key (CONN-06 — the UI never echoes
    /// the key back, so a blank submission means "unchanged", never "clear it"); a non-empty value replaces
    /// it. A null <paramref name="baseUrl"/>/<paramref name="selectedVersion"/> likewise preserves the
    /// stored value.
    /// </summary>
    public WhisparrOptions WithSubmitted(
        string? baseUrl, string? apiKey, string? selectedVersion, int rootFolderId, int qualityProfileId)
        => this; // RED stub — GREEN applies the submitted values with write-only key semantics
}

/// <summary>
/// The redaction-safe projection of <see cref="WhisparrOptions"/> returned to the settings UI: it carries
/// every persisted field EXCEPT the API key, replacing it with a <c>hasApiKey</c> boolean (CONN-06). No
/// response ever serializes the raw key.
/// </summary>
public sealed record OptionsView(
    string BaseUrl,
    string SelectedVersion,
    string DetectedVersion,
    int RootFolderId,
    int QualityProfileId,
    [property: JsonPropertyName("hasApiKey")] bool HasApiKey)
{
    /// <summary>Projects the stored options to the redaction-safe view (the raw key is dropped here).</summary>
    public static OptionsView From(WhisparrOptions options)
        => new(options.BaseUrl, options.SelectedVersion, options.DetectedVersion,
            options.RootFolderId, options.QualityProfileId, HasApiKey: false); // RED stub — GREEN derives from ApiKey
}
