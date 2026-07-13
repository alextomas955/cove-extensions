namespace WhisparrSync.Client;

/// <summary>
/// The Whisparr v3 <c>GET /api/v3/system/status</c> projection. Field set transcribed from the
/// devopsarr/whisparr-go generated <c>SystemResource</c> model; only the fields this phase needs are
/// modeled. <see cref="Version"/> is the dotted app version (e.g. <c>3.3.4.808</c>) whose major segment
/// the adapter gate reads; <see cref="InstanceName"/> is shown on a successful Test connection.
/// </summary>
internal sealed record SystemStatus(string? Version, string? AppName, string? InstanceName, string? Branch);
