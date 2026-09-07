using System.Text.Json.Serialization;
using WhisparrSync.Ingest;

namespace WhisparrSync.State;

/// <summary>
/// Source-generated (trim-safe, zero-reflection) JSON context for the ingest surface: the inbound
/// webhook body (parsed case-insensitively — Whisparr emits camelCase) and the persisted ingest blobs
/// (the <see cref="EventLedger"/> key set, the import-log audit journal and the per-dependency health
/// record). Mirrors
/// <c>WhisparrJsonContext</c> / <c>MatchJsonContext</c> so the serialization stays zero-warning under
/// <c>TreatWarningsAsErrors</c>.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WebhookPayload))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ImportStatus))]
[JsonSerializable(typeof(CheckpointState))]
[JsonSerializable(typeof(DependencyHealth[]))]
internal sealed partial class IngestJsonContext : JsonSerializerContext;
