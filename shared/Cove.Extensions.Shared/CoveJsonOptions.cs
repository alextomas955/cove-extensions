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
    /// Returns a NEW instance per call so each caller keeps its own (independently frozen-on-first-use)
    /// options object, exactly as separate inline initializers did.
    /// </remarks>
    public static JsonSerializerOptions WebWithEnumStrings() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}
