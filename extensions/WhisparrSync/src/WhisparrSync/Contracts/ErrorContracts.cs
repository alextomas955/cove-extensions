namespace WhisparrSync.Contracts;

// The refusal and outage bodies, which are a wire contract like any other response. There are three
// discriminator keys here rather than one, and they are NOT interchangeable: the UI reads `code` through
// errorCodeLogic/actionFailureLogic, `error` on the activity surfaces, and `result` on the connect + monitor
// paths. Each family keeps its own key.
//
// Shape-per-record rather than one body with optional members: several sites pass a nullable value that rides
// the wire as an explicit null (a not-yet-probed DetectedVersion, an absent Reason). Folding those into shared
// optional members would need WhenWritingNull, which turns those nulls into absent keys — a wire change.

/// <summary>A refusal carrying only its machine-readable code.</summary>
internal sealed record ErrorResponse(string Code);

/// <summary>
/// The refusal a route sends when the connected generation cannot honor it.
/// </summary>
/// <remarks><see cref="Detected"/> is null until a probe against the stored host has succeeded.</remarks>
internal sealed record VersionUnsupportedResponse(string Code, string? Detected);

/// <summary>
/// A 200 refusal for an entity Cove holds no usable remote id for — a state the panel renders rather than an
/// error, and one that makes no outbound call.
/// </summary>
/// <remarks><see cref="Provider"/> names the metadata source the connected generation keys on, so the UI can
/// say which link is missing without knowing the version rule.</remarks>
internal sealed record NoIdentityResponse(string Code, string Provider);

/// <summary>A batch refused before any per-item work because it exceeded <see cref="Max"/> ids.</summary>
internal sealed record TooManyIdsResponse(string Code, int Max);

/// <summary>
/// The stored-configuration refusal. <see cref="Options"/> carries option KEYS only — a read-gated caller
/// learns which setting is unset without learning what is stored.
/// </summary>
internal sealed record ConfigIncompleteResponse(string Code, IReadOnlyList<string> Options);

/// <summary>
/// An add the user's own Whisparr import exclusions cover: a handled outcome carrying Whisparr's reason, not
/// an error.
/// </summary>
internal sealed record ExcludedResponse(string Code, string? Message);

/// <summary>
/// An outcome the caller branches on by <see cref="Result"/> alone — a failure discriminator, or a connect
/// attempt that reached the host and was rejected by it.
/// </summary>
/// <remarks>The discriminator is deliberately coarse: it never carries the key, the host, or a raw reason.</remarks>
internal sealed record ResultDiscriminatorResponse(string Result);

/// <summary>A mutation Whisparr itself refused, carrying its error text (safe to surface — not the key/URL).</summary>
internal sealed record RejectedResponse(string Result, string? Message);

/// <summary>
/// An activity read that could not be answered.
/// </summary>
/// <remarks>
/// Distinct from an empty page on purpose: an empty activity list is a healthy answer, so an outage must not
/// ride the wire as a silently-empty 200.
/// </remarks>
internal sealed record ActivityOutageResponse(string Error);
