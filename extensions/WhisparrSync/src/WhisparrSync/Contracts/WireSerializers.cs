using System.Text.Json;
using Cove.Extensions.Shared;

namespace WhisparrSync.Contracts;

/// <summary>
/// The two response <c>JsonSerializerOptions</c> statics every handler serializes through. The host's default
/// minimal-API serializer is not relied on: each response names the one below matching its contract.
/// </summary>
internal static class WireSerializers
{
    // No enum converter is registered here, so an enum reaching a response through this options object would
    // serialize as an integer. Every enum that does carries its own type-level converter (SceneWhisparrState);
    // a response with a bare enum value names EnumStringResponseJsonOptions below instead.
    internal static readonly JsonSerializerOptions WebResponseJsonOptions = new(JsonSerializerDefaults.Web);

    // Adds camelCase enum strings. Responses carrying a bare enum value use this one; it would otherwise reach
    // the UI as an integer.
    internal static readonly JsonSerializerOptions EnumStringResponseJsonOptions = CoveJsonOptions.WebWithEnumStrings();
}
