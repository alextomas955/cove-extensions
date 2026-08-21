using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cove.Extensions.Shared;

/// <summary>
/// A <see cref="JsonStringEnumConverter"/> fixed to <see cref="JsonNamingPolicy.CamelCase"/>, so an enum
/// can carry it as <c>[JsonConverter(typeof(CamelCaseStringEnumConverter))]</c>.
/// </summary>
/// <remarks>
/// <para>
/// A <c>[JsonConverter]</c> attribute names a type and nothing else — the converter is built through a
/// parameterless constructor — so there is no way to pass a naming policy through one. That is the only
/// reason this derived type exists.
/// </para>
/// <para>
/// Declaring the converter on the ENUM is what makes the wire spelling a property of the type rather
/// than of whichever options object happens to be serializing: the response bytes, the persisted scan
/// blob and the emitted OpenAPI schema all read this one declaration. Keep it that way. An equivalent
/// converter added to a <see cref="JsonSerializerOptions.Converters"/> collection does not merely
/// duplicate this — it OUTRANKS it (the documented precedence is property attribute, then the options
/// collection, then the type attribute), so a second copy that drifted would win silently.
/// </para>
/// </remarks>
public sealed class CamelCaseStringEnumConverter() : JsonStringEnumConverter(JsonNamingPolicy.CamelCase);
