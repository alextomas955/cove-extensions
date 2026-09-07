using System.Text.Json.Serialization;

namespace WhisparrSync.Discovery;

/// <summary>
/// Source-generated (trim-safe, zero-reflection) JSON context for the hand-rolled StashDB GraphQL DTOs — the
/// codegen substitute: these pinned type infos are the only serialize/deserialize path, and the
/// offline shape test locks the response fields. camelCase policy matches StashDB's wire (and GraphQL's
/// <c>query</c>/<c>variables</c>); the two snake_case fields (<c>per_page</c>, <c>release_date</c>) pin
/// their names explicitly on the DTO.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    // Drop null request fields so a studio-only or performer-only query never emits the other criterion as an
    // explicit null (StashDB rejects an empty/null criterion); deserialize-only DTOs are unaffected.
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StashDbGraphQlRequest))]
[JsonSerializable(typeof(StashDbGraphQlResponse))]
[JsonSerializable(typeof(StashDbTagNameRequest))]
[JsonSerializable(typeof(StashDbTagLookupResponse))]
[JsonSerializable(typeof(StashDbPerformerQueryRequest))]
[JsonSerializable(typeof(StashDbPerformerQueryResponse))]
[JsonSerializable(typeof(StashDbPerformerStudiosRequest))]
[JsonSerializable(typeof(StashDbPerformerStudiosResponse))]
[JsonSerializable(typeof(StashDbScenePerformer))]
[JsonSerializable(typeof(StashDbSceneTag))]
[JsonSerializable(typeof(StashDbDateCriterion))]
internal sealed partial class StashDbJsonContext : JsonSerializerContext;
