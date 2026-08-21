using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cove.Extensions.Shared;

/// <summary>
/// A <see cref="JsonStringEnumConverter"/> fixed to <see cref="JsonNamingPolicy.CamelCase"/>, so an enum
/// can carry it as <c>[JsonConverter(typeof(CamelCaseStringEnumConverter))]</c>.
/// </summary>
/// <remarks>
/// A <c>[JsonConverter]</c> attribute names a type and nothing else, so there is no way to pass a naming
/// policy through one; that is the only reason this derived type exists.
/// <para>
/// Declare it on the ENUM. An equivalent converter in a <see cref="JsonSerializerOptions.Converters"/>
/// collection does not duplicate this one, it OUTRANKS it — the precedence is property attribute, then
/// the options collection, then the type attribute — so a second copy that drifted would win silently.
/// </para>
/// </remarks>
public sealed class CamelCaseStringEnumConverter() : JsonStringEnumConverter(JsonNamingPolicy.CamelCase);
