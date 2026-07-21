using System.Text.Json.Serialization;

namespace WhisparrSync.Matching;

/// <summary>
/// Source-generated (trim-safe, zero-reflection) JSON context for the match-map blob. The enums render
/// as strings (<c>UseStringEnumConverter</c>) so a hand-read blob is legible and forward-compatible —
/// and so the serialization stays zero-warning under <c>TreatWarningsAsErrors</c> (a reflection-based
/// <c>JsonStringEnumConverter</c> instance would trip the trim analyzer).
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(MatchState[]))]
internal sealed partial class MatchJsonContext : JsonSerializerContext;
