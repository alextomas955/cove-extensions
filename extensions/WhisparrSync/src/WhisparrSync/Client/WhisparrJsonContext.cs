using System.Text.Json.Serialization;

namespace WhisparrSync.Client;

/// <summary>
/// Source-generated (trim-safe, zero-reflection) JSON context for the Whisparr DTOs. Whisparr emits
/// camelCase, so case-insensitive matching binds its <c>version</c>/<c>instanceName</c> onto the
/// PascalCase record properties.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SystemStatus))]
internal sealed partial class WhisparrJsonContext : JsonSerializerContext;
