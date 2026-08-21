using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cove.Extensions.Shared;

/// <summary>Shared <see cref="JsonSerializerOptions"/> factories for extension response contracts.</summary>
public static class CoveJsonOptions
{
    /// <summary>
    /// A fresh options instance using the camelCase Web convention plus a
    /// <see cref="JsonStringEnumConverter"/> so enum-typed fields serialize as their string names
    /// (the shape the UI matches) rather than integers.
    /// </summary>
    /// <remarks>
    /// The converter carries <see cref="JsonNamingPolicy.CamelCase"/>, so enum VALUES emit camelCase
    /// (e.g. <c>needsReview</c>) to match the all-camelCase wire — the C# enum members keep their PascalCase
    /// identifiers; only the wire string is re-cased. A type-level converter declared on an enum itself
    /// still takes precedence, so that enum's own casing is unaffected.
    /// Returns a NEW instance per call so each caller keeps its own (independently frozen-on-first-use)
    /// options object, exactly as separate inline initializers did.
    /// </remarks>
    public static JsonSerializerOptions WebWithEnumStrings() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}

/// <summary>
/// A <see cref="JsonStringEnumConverter"/> fixed to <see cref="JsonNamingPolicy.CamelCase"/>, so an enum
/// can carry it as <c>[JsonConverter(typeof(CamelCaseStringEnumConverter))]</c>.
/// </summary>
/// <remarks>
/// A <c>[JsonConverter]</c> attribute cannot pass a naming policy, which is the only reason this derived
/// type exists. Declaring the converter on the ENUM rather than on an options instance is what makes the
/// wire spelling a property of the type: the response bytes, the persisted blob and the OpenAPI schema
/// all read the same one declaration, so no two of them can be configured to disagree.
/// </remarks>
public sealed class CamelCaseStringEnumConverter() : JsonStringEnumConverter(JsonNamingPolicy.CamelCase);
