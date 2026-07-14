using System.Text.Json.Serialization;
using WhisparrSync.Ingest;

namespace WhisparrSync.State;

/// <summary>
/// Source-generated (trim-safe, zero-reflection) JSON context for the ingest surface: the inbound
/// webhook body (parsed case-insensitively — Whisparr emits camelCase) and the persisted ingest blobs
/// (the <see cref="EventLedger"/> key set and, from 03-01 Task 3, the import-log audit journal). Mirrors
/// <c>WhisparrJsonContext</c> / <c>MatchJsonContext</c> so the serialization stays zero-warning under
/// <c>TreatWarningsAsErrors</c>.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(WebhookPayload))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ImportLogEntry[]))]
internal sealed partial class IngestJsonContext : JsonSerializerContext;
