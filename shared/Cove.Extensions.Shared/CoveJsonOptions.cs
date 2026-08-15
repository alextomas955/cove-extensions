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
    /// The host's default minimal-API serializer is camelCase but emits enums as NUMBERS, so a client
    /// matching on an enum's string name reads every value as the wrong case — with a valid <c>200</c>
    /// and no error anywhere. Extension code cannot reach host startup
    /// (<c>ConfigureHttpJsonOptions</c>) to register a converter globally, so a response serializes
    /// with these options instead.
    /// <para>
    /// The converter carries <see cref="JsonNamingPolicy.CamelCase"/>, so enum VALUES emit camelCase
    /// (e.g. <c>needsReview</c>) to match the camelCase property names of the same responses — the C#
    /// enum members keep their PascalCase identifiers; only the wire string is re-cased. This governs
    /// what an extension WRITES as a response, and nothing else: a request body an extension parses for
    /// itself answers to whatever options that parse names, which is how a PascalCase blob can travel
    /// over the same wire. A type-level converter declared on an enum itself still takes precedence, so
    /// that enum's own casing is unaffected.
    /// </para>
    /// Returns a NEW instance per call so each caller keeps its own (independently frozen-on-first-use)
    /// options object, exactly as separate inline initializers did.
    /// </remarks>
    public static JsonSerializerOptions WebWithEnumStrings() => new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
