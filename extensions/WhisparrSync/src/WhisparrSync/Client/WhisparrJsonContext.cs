using System.Text.Json.Serialization;

namespace WhisparrSync.Client;

/// <summary>
/// Source-generated (trim-safe, zero-reflection) JSON context for the Whisparr DTOs. Whisparr emits
/// camelCase, so case-insensitive matching binds its <c>version</c>/<c>instanceName</c> onto the
/// PascalCase record properties.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SystemStatus))]
[JsonSerializable(typeof(RootFolder[]))]
[JsonSerializable(typeof(QualityProfile[]))]
[JsonSerializable(typeof(WhisparrMovie))]
[JsonSerializable(typeof(WhisparrMovie[]))]
[JsonSerializable(typeof(WhisparrHistoryPage))]
[JsonSerializable(typeof(WhisparrSeries))]
[JsonSerializable(typeof(WhisparrSeries[]))]
[JsonSerializable(typeof(WhisparrEpisode[]))]
[JsonSerializable(typeof(WhisparrEpisodeFile[]))]
[JsonSerializable(typeof(WhisparrStudio))]
[JsonSerializable(typeof(WhisparrStudio[]))]
[JsonSerializable(typeof(WhisparrPerformer))]
[JsonSerializable(typeof(WhisparrPerformer[]))]
[JsonSerializable(typeof(WhisparrTag))]
[JsonSerializable(typeof(WhisparrTag[]))]
[JsonSerializable(typeof(WhisparrExclusion))]
[JsonSerializable(typeof(WhisparrExclusion[]))]
[JsonSerializable(typeof(WhisparrRelease[]))]
[JsonSerializable(typeof(WhisparrManualImportItem[]))]
[JsonSerializable(typeof(NamingConfig))]
[JsonSerializable(typeof(MediaManagementConfig))]
[JsonSerializable(typeof(WhisparrCommand))]
internal sealed partial class WhisparrJsonContext : JsonSerializerContext;
