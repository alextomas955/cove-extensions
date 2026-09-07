namespace WhisparrSync.Contracts;

/// <summary>The Test-connection request body: the URL + key the user typed on the settings page.</summary>
internal sealed record TestConnectionRequest(string? BaseUrl, string? ApiKey);

/// <summary>
/// The <c>/register-webhook</c> body: the (possibly hand-edited) webhook <see cref="Url"/> the UI shows.
/// Only its origin (scheme+host) is honored server-side; the token is always re-minted from the stored secret.
/// Null (an empty POST) falls back to the request host, preserving the pre-edit behavior.
/// </summary>
internal sealed record WebhookRegisterRequest(string? Url);

/// <summary>
/// One saved connection's webhook registration answer, for the settings page's Import-webhook section.
/// </summary>
/// <remarks>
/// The webhook URL embeds the shared secret, which is why this record has no URL member: the URL is returned
/// once, for the active connection only, as <see cref="WebhookUrlResponse.Url"/>. <see cref="Registered"/> is
/// false both when the connector is absent and when that one instance could not be read.
/// </remarks>
internal sealed record WebhookConnectionView(string Version, string BaseUrl, bool Registered);

/// <summary>
/// The <c>/webhook-url</c> response: the webhook <see cref="Url"/> to show and whether Whisparr's own
/// "Cove Whisparr Sync" connection currently exists (<see cref="Registered"/>). When that connection is
/// present the URL is ITS stored url — Whisparr is the source of truth — and <see cref="Registered"/> is
/// true; otherwise the URL is the derived default (the persisted host, else the request host) and
/// <see cref="Registered"/> is false.
/// </summary>
/// <remarks>
/// <see cref="Url"/> and <see cref="Registered"/> describe the ACTIVE connection only; <see cref="Connections"/>
/// answers the same question per saved connection. A version with no saved connection has no entry at all,
/// rather than an entry reading false.
/// </remarks>
internal sealed record WebhookUrlResponse(
    string Url, bool Registered, IReadOnlyList<WebhookConnectionView> Connections);

/// <summary>
/// The <c>/status</c> projection: whether the stored configuration is complete enough to act on.
/// </summary>
/// <remarks>
/// <see cref="MissingRequiredOptions"/> carries option KEYS only, never a URL, a key value, or anything
/// adjacent to one — a read-gated caller learns which setting is unset without learning what is stored.
/// <see cref="DetectedVersion"/> is empty until a probe against the stored host succeeds, which is an honest
/// not-yet-verified state rather than a failed detection.
/// </remarks>
internal sealed record ConfigStatusResponse(
    bool Configured, bool HasApiKey, string? DetectedVersion, IReadOnlyList<string> MissingRequiredOptions);

/// <summary>The <c>/test-connection</c> answer for a reachable instance on a supported generation.</summary>
internal sealed record TestConnectionSuccessResponse(string Result, string? Version, string? InstanceName);

/// <summary>
/// The <c>/test-connection</c> answer for an instance whose major version no adapter serves.
/// </summary>
/// <remarks>Branching is on the PARSED version, never the status code — a v2 instance also answers <c>/api/v3</c>.</remarks>
internal sealed record TestConnectionVersionMismatchResponse(string Result, string? Detected);

/// <summary>
/// The <c>/test-connection</c> answer for an instance that could not be reached, carrying a short reason.
/// </summary>
/// <remarks>The reason is the transport's own classification — never the key or the host.</remarks>
internal sealed record TestConnectionUnreachableResponse(string Result, string? Reason);

/// <summary>
/// The <c>/register-webhook</c> answer.
/// </summary>
/// <remarks><see cref="Registered"/> is false both when no adapter serves the stored version and when the
/// registration call itself failed — the settings page retries either way.</remarks>
internal sealed record WebhookRegistrationResponse(bool Registered);
