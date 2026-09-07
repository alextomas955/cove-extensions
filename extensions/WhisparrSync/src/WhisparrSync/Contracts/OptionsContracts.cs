using WhisparrSync.Options;

namespace WhisparrSync.Contracts;

/// <summary>
/// The options-save request body. Case-insensitive minimal-API binding maps the UI's PascalCase JSON
/// onto these. An empty/absent <see cref="ApiKey"/> preserves the stored key (write-only). The
/// add-defaults (<see cref="TagsOnAdd"/>, <see cref="MonitorNewByDefault"/>,
/// <see cref="AllowQualityUpgrades"/>) are nullable so an absent field PRESERVES the stored value
/// (a partial save never resets an unrelated toggle — <see cref="WhisparrOptions.WithSubmitted"/>).
/// The design's "Search on add" is deliberately NOT a field here: loop-safety is LOCKED, so an add never
/// auto-searches and there is nothing for the UI to bind to a wire toggle.
/// </summary>
internal sealed record OptionsSaveRequest(
    string? BaseUrl, string? ApiKey, string? SelectedVersion,
    IReadOnlyList<PathTranslationRule>? PathTranslation = null,
    IReadOnlyList<string>? TagsOnAdd = null, bool? MonitorNewByDefault = null, bool? AllowQualityUpgrades = null);
