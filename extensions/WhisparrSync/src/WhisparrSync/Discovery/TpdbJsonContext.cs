using System.Text.Json.Serialization;

namespace WhisparrSync.Discovery;

/// <summary>
/// Source-generated (trim-safe, zero-reflection) JSON context for the hand-rolled ThePornDB REST DTOs — the
/// codegen substitute: this pinned type info is the only deserialize path, and the offline shape test
/// locks the response fields. The snake_case wire names (<c>current_page</c>, <c>last_page</c>) are pinned
/// per-property on the DTOs.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(TpdbScenesResponse))]
[JsonSerializable(typeof(TpdbTagLookupResponse))]
[JsonSerializable(typeof(TpdbPerformerParent))]
internal sealed partial class TpdbJsonContext : JsonSerializerContext;
